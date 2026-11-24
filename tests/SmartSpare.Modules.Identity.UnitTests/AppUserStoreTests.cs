using Microsoft.AspNetCore.Identity;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Identity;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// The Identity store translates Identity's calls into the aggregate's behaviour (STAGE_3_PLAN 2.1–2.6). These calls
/// touch no database; queries are covered by the integration tests.
/// </summary>
public sealed class AppUserStoreTests : IDisposable
{
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);

    // Never opened: the tested calls don't query.
    private readonly IdentityDbContext _db = IdentityDatabase.Instance.CreateContext("Host=localhost;Database=unused");

    private static AppUser NewUser() => AppUser.Create(
        Guid.NewGuid(), UserName.Create("tomas.r").Value, EmailAddress.Create("t@dealer.example").Value, "T R", null, Hash);

    [Fact]
    public async Task A_proposed_base32_stamp_is_replaced_by_a_new_guid()
    {
        using var store = new AppUserStore(_db);
        var user = NewUser();
        var before = await store.GetSecurityStampAsync(user, Ct);

        await store.SetSecurityStampAsync(user, "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", Ct);

        var after = await store.GetSecurityStampAsync(user, Ct);
        after.ShouldNotBe(before);
        after.ShouldNotBe("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP");
        Guid.ParseExact(after!, "N").ShouldBe(user.SecurityStamp);
    }

    [Fact]
    public async Task Lockout_calls_map_onto_the_aggregate()
    {
        using var store = new AppUserStore(_db);
        var user = NewUser();
        var end = DateTimeOffset.UtcNow.AddMinutes(15);

        (await store.IncrementAccessFailedCountAsync(user, Ct)).ShouldBe(1);
        (await store.IncrementAccessFailedCountAsync(user, Ct)).ShouldBe(2);
        (await store.GetAccessFailedCountAsync(user, Ct)).ShouldBe(2);

        await store.SetLockoutEndDateAsync(user, end, Ct);
        await store.ResetAccessFailedCountAsync(user, Ct);

        user.LockoutEnd.ShouldBe(end);
        user.FailedLoginCount.ShouldBe((short)0);
        (await store.GetLockoutEndDateAsync(user, Ct)).ShouldBe(end);
        (await store.GetLockoutEnabledAsync(user, Ct)).ShouldBeTrue("lockout applies to every user");
    }

    [Fact]
    public async Task A_new_password_hash_goes_through_the_aggregate()
    {
        using var store = new AppUserStore(_db);
        var user = NewUser();
        var stamp = user.SecurityStamp;
        var newHash = "AQAAAAIAA1tgAAAAE" + new string('B', 67);

        await store.SetPasswordHashAsync(user, newHash, Ct);

        user.PasswordHash.ShouldBe(newHash);
        user.SecurityStamp.ShouldNotBe(stamp);
        await Should.ThrowAsync<NotSupportedException>(() => store.SetPasswordHashAsync(user, null, Ct));
    }

    [Fact]
    public async Task The_normalized_name_is_the_stored_lower_case_name()
    {
        using var store = new AppUserStore(_db);
        var user = NewUser();

        (await store.GetNormalizedUserNameAsync(user, Ct)).ShouldBe("tomas.r");
        new LowerInvariantLookupNormalizer().NormalizeName("Tomas.R").ShouldBe("tomas.r");
        (await store.FindByNameAsync("not a valid name!", Ct)).ShouldBeNull("an invalid name cannot exist");
    }

    [Fact]
    public async Task User_administration_is_not_available_in_stage_3()
    {
        using var store = new AppUserStore(_db);

        await Should.ThrowAsync<NotSupportedException>(() => store.CreateAsync(NewUser(), Ct));
        await Should.ThrowAsync<NotSupportedException>(() => store.AddToRoleAsync(NewUser(), AppRoles.Administrator, Ct));
    }

    [Fact]
    public async Task Only_active_users_may_sign_in()
    {
        var confirmation = new ActiveUserConfirmation();
        var user = NewUser();

        (await confirmation.IsConfirmedAsync(null!, user)).ShouldBeTrue();
        user.Deactivate(DateTimeOffset.UtcNow);
        (await confirmation.IsConfirmedAsync(null!, user)).ShouldBeFalse();
    }

    [Fact]
    public void Identity_options_take_the_lockout_from_the_one_policy()
    {
        var options = new IdentityOptions();

        IdentityModuleInstaller.ConfigureIdentity(options);

        options.Lockout.MaxFailedAccessAttempts.ShouldBe(LockoutPolicy.Default.MaxFailedAttempts);
        options.Lockout.DefaultLockoutTimeSpan.ShouldBe(LockoutPolicy.Default.Duration);
        options.Lockout.AllowedForNewUsers.ShouldBeTrue();
        options.SignIn.RequireConfirmedAccount.ShouldBeTrue();
        options.ClaimsIdentity.UserIdClaimType.ShouldBe(AppClaimTypes.Subject);
        options.ClaimsIdentity.UserNameClaimType.ShouldBe(AppClaimTypes.Name);
        options.ClaimsIdentity.RoleClaimType.ShouldBe(AppClaimTypes.Role);
        options.ClaimsIdentity.SecurityStampClaimType.ShouldBe(IdentityClaimTypes.SecurityStamp);
        options.Password.RequiredLength.ShouldBe(12);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
