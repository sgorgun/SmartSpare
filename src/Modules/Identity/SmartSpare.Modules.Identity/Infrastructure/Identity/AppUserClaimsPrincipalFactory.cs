using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>
/// Builds the claims of a session (STAGE_3_PLAN section 3.1): <c>sub</c>, <c>name</c>, the security stamp (added by
/// Identity), <c>role</c> per role code, <c>ss:brand</c> per brand and <c>ss:warehouse</c> per hub. Every role, brand
/// and hub is an effective right derived by <see cref="EffectiveRightsCalculator"/> from the organisation (ADR 0026).
/// No personal data besides the user name: no e-mail, full name or phone.
/// </summary>
internal sealed class AppUserClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options, OrganisationReader organisation)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);
        var rights = await organisation.RightsForClaimsAsync(user, CancellationToken.None).ConfigureAwait(false);

        foreach (var role in rights.Roles)
        {
            identity.AddClaim(new Claim(AppClaimTypes.Role, role));
        }

        foreach (var brand in rights.BrandIds.Order())
        {
            identity.AddClaim(new Claim(AppClaimTypes.Brand, brand.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var hub in rights.HubIds.Order())
        {
            identity.AddClaim(new Claim(AppClaimTypes.Warehouse, hub.ToString(CultureInfo.InvariantCulture)));
        }

        return identity;
    }
}

/// <summary>
/// Only an active, not anonymised account of an employee who has not left may sign in (section 2.6, ADR 0026).
/// <c>SignInManager.CanSignInAsync</c> asks this when <c>SignIn.RequireConfirmedAccount</c> is set, at sign-in and at
/// every session check, so archiving an employee blocks sign-in at once.
/// </summary>
internal sealed class ActiveUserConfirmation(OrganisationReader organisation) : IUserConfirmation<AppUser>
{
    public async Task<bool> IsConfirmedAsync(UserManager<AppUser> manager, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsActive && user.AnonymisedAt is null
            && await organisation.EmployeeIsActiveAsync(user.EmployeeId, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// User names are stored in canonical lower case (<see cref="UserName"/>), so the normalized form is the lower-case
/// form and equals the stored value (section 2.2). E-mail is not used for sign-in.
/// </summary>
internal sealed class LowerInvariantLookupNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => name?.ToLowerInvariant();

    public string? NormalizeEmail(string? email) => email?.ToLowerInvariant();
}
