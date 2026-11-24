using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>
/// Builds the claims of a session (STAGE_3_PLAN section 3.1): <c>sub</c>, <c>name</c>, the security stamp (added by
/// Identity), <c>role</c> per role code, <c>ss:brand</c> per brand and <c>ss:warehouse</c>. No personal data besides
/// the user name: no e-mail, full name or phone.
/// </summary>
internal sealed class AppUserClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        foreach (var role in await UserManager.GetRolesAsync(user).ConfigureAwait(false))
        {
            identity.AddClaim(new Claim(AppClaimTypes.Role, role));
        }

        foreach (var brand in user.Brands.Select(b => b.BrandId).Order())
        {
            identity.AddClaim(new Claim(AppClaimTypes.Brand, brand.ToString(CultureInfo.InvariantCulture)));
        }

        if (user.HomeWarehouseId is { } warehouse)
        {
            identity.AddClaim(new Claim(AppClaimTypes.Warehouse, warehouse.ToString(CultureInfo.InvariantCulture)));
        }

        return identity;
    }
}

/// <summary>
/// Only active, not anonymised users may sign in (section 2.6). <c>SignInManager.CanSignInAsync</c> asks this when
/// <c>SignIn.RequireConfirmedAccount</c> is set.
/// </summary>
internal sealed class ActiveUserConfirmation : IUserConfirmation<AppUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<AppUser> manager, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.IsActive && user.AnonymisedAt is null);
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
