using System.Net;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Modules.Identity;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Sign-in through the real Identity module against PostgreSQL 17 (STAGE_3_PLAN 2 and 9.2): password check, rehash,
/// uniform failures, lockout, the audit actor, and session revocation by the security stamp. The host connects as
/// <c>smartspare_api</c>, so these also prove the login's privileges are sufficient.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class SignInTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private HostFactory _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_seeded_user_signs_in_and_gets_roles_brands_and_last_login()
    {
        var id = await database.CreateUserAsync("it.signin", [AppRoles.BrandManager, AppRoles.Sponsor], ["CLAAS", "LEMKEN"]);
        using var browser = _host.NewBrowser();

        using var response = await browser.LoginAsync("IT.SignIn", IdentityDatabaseFixture.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "user names are case-insensitive");
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(BffDefaults.CookieName + "=", StringComparison.Ordinal));
        var me = await browser.MeAsync();
        me.UserId.ShouldBe(id);
        me.UserName.ShouldBe("it.signin");
        me.Roles.ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor]);
        me.BrandIds.ShouldBe(await database.BrandIdsAsync("CLAAS", "LEMKEN"));
        (await database.ScalarAsync<bool>("SELECT last_login_at IS NOT NULL FROM iam.app_user WHERE id = @id", ("id", id))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_100000_iteration_hash_signs_in_and_is_rehashed_with_220000()
    {
        var id = await database.CreateUserAsync("it.rehash", [AppRoles.InventoryPlanner], [], iterations: 100_000);
        using var browser = _host.NewBrowser();

        await LoginOrFailAsync(browser, "it.rehash");

        var hash = await database.ScalarAsync<string>("SELECT password_hash FROM iam.app_user WHERE id = @id", ("id", id));
        Iterations(hash).ShouldBe(220_000u);
        Hasher().VerifyHashedPassword(null!, hash, IdentityDatabaseFixture.Password).ShouldBe(PasswordVerificationResult.Success);
    }

    [Fact]
    public async Task Wrong_password_unknown_user_deactivated_and_anonymised_users_get_identical_responses()
    {
        await database.CreateUserAsync("it.uniform", [AppRoles.BrandManager], []);
        var deactivated = await database.CreateUserAsync("it.inactive", [AppRoles.BrandManager], []);
        await database.ExecuteAsync("UPDATE iam.app_user SET is_active = false WHERE id = @id", ("id", deactivated));
        var anonymised = await database.CreateUserAsync("it.anonymised", [AppRoles.BrandManager], []);
        await database.ExecuteAsync(
            "UPDATE iam.app_user SET is_active = false, password_hash = NULL, anonymised_at = now() WHERE id = @id", ("id", anonymised));
        using var browser = _host.NewBrowser();

        var problems = new List<Problem>();
        foreach (var (name, password) in new[]
                 {
                     ("it.uniform", "wrong password"),
                     ("it.nobody", IdentityDatabaseFixture.Password),
                     ("it.inactive", IdentityDatabaseFixture.Password),
                     ("it.anonymised", IdentityDatabaseFixture.Password),
                 })
        {
            using var response = await browser.LoginAsync(name, password);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, name);
            problems.Add((await Problem.ReadAsync(response)).WithoutTraceId());
        }

        problems.Distinct().ShouldHaveSingleItem().Code.ShouldBe(ProblemCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_until_lockout_end_passes()
    {
        var id = await database.CreateUserAsync("it.lockout", [AppRoles.BrandManager], []);
        using var browser = _host.NewBrowser();
        var token = await browser.AntiforgeryTokenAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await browser.LoginAsync("it.lockout", "wrong password", token: token);
            failed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Identity locked the account for 15 minutes and reset the counter, so it stays far below the CHECK's 100.
        (await database.ScalarAsync<bool>(
            "SELECT lockout_end BETWEEN now() + interval '14 minutes' AND now() + interval '16 minutes' AND failed_login_count = 0 FROM iam.app_user WHERE id = @id",
            ("id", id))).ShouldBeTrue();

        // The lockout is written as the user who was signing in, with the reason 'sign-in' (section 2.2).
        (await database.ScalarAsync<bool>(
            """
            SELECT user_id = @id AND reason = 'sign-in' AND 'lockout_end' = ANY(changed_columns)
            FROM audit.audit_log WHERE table_name = 'iam.app_user' AND record_id = @record AND action = 'UPDATE'
            ORDER BY id DESC LIMIT 1
            """,
            ("id", id), ("record", id.ToString()))).ShouldBeTrue();

        using (var locked = await browser.LoginAsync("it.lockout", IdentityDatabaseFixture.Password, token: token))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the correct password is refused while locked out");
            (await Problem.ReadAsync(locked)).Code.ShouldBe(ProblemCodes.InvalidCredentials);
        }

        // UserManager in ASP.NET Core 10 compares lockout_end with DateTimeOffset.UtcNow, not TimeProvider, so the
        // test moves lockout_end into the past: the same effect as an early unlock by an administrator (#46).
        await database.ExecuteAsync("UPDATE iam.app_user SET lockout_end = now() - interval '1 second' WHERE id = @id", ("id", id));
        using var unlocked = await browser.LoginAsync("it.lockout", IdentityDatabaseFixture.Password, token: token);
        unlocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_rotated_security_stamp_ends_the_session_at_the_next_check()
    {
        var id = await database.CreateUserAsync("it.revoked", [AppRoles.BrandManager], []);
        using var browser = _host.NewBrowser();
        await LoginOrFailAsync(browser, "it.revoked");

        await database.ExecuteAsync("UPDATE iam.app_user SET security_stamp = gen_random_uuid() WHERE id = @id", ("id", id));
        _host.Time.Advance(BffDefaults.SessionCheckInterval);

        using var response = await browser.GetAsync("/api/identity/ping");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.MeAsync()).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_role_change_reaches_the_cookie_at_the_next_check()
    {
        var id = await database.CreateUserAsync("it.promoted", [AppRoles.BrandManager], []);
        using var browser = _host.NewBrowser();
        await LoginOrFailAsync(browser, "it.promoted");

        await database.ExecuteAsync(
            "INSERT INTO iam.user_role (user_id, role_id) SELECT @id, id FROM iam.role WHERE code = 'SPONSOR'", ("id", id));
        _host.Time.Advance(BffDefaults.SessionCheckInterval);

        (await browser.MeAsync()).Roles.ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor]);
    }

    [Fact]
    public async Task The_cookie_and_an_internal_token_minted_from_it_see_the_same_user()
    {
        await database.CreateUserAsync("it.both", [AppRoles.WarehouseKeeper], ["JD"]);
        using var browser = _host.NewBrowser();
        await LoginOrFailAsync(browser, "it.both");
        var me = await browser.MeAsync();
        var principal = TestUser.ClaimsFor(me.UserId!.Value, me.UserName!, me.Roles, me.BrandIds, me.HomeWarehouseId);

        using var other = _host.NewBrowser();
        var viaToken = await other.MeAsync(bearer: _host.Token(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(principal))));

        viaToken.ShouldBeEquivalentTo(me);
    }

    [Fact]
    public async Task Two_host_instances_on_one_database_accept_each_others_cookies()
    {
        // #22: the key ring lives in iam.data_protection_keys, so every instance can read the cookie of another.
        await database.CreateUserAsync("it.shared", [AppRoles.BrandManager], []);
        await using var second = new HostFactory { ConnectionString = database.ApiConnectionString };
        using var first = _host.NewBrowser();
        using var signIn = await first.LoginAsync("it.shared", IdentityDatabaseFixture.Password);
        signIn.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(BffDefaults.CookieName + "=", StringComparison.Ordinal));

        using var other = second.NewBrowser();
        other.Client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        var me = await other.MeAsync();

        me.IsAuthenticated.ShouldBeTrue("the second instance decrypts the first instance's cookie");
        me.UserName.ShouldBe("it.shared");

        // Both instances read the ring from the database, not from a per-user key directory that in-process test
        // hosts would also share.
        foreach (var host in new[] { _host, second })
        {
            var repository = host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
            repository.ShouldNotBeNull();
            repository.GetType().GetGenericTypeDefinition().ShouldBe(typeof(EntityFrameworkCoreXmlRepository<>),
                "keys must be stored in iam.data_protection_keys");
        }

        (await database.ScalarAsync<long>("SELECT count(*) FROM iam.data_protection_keys")).ShouldBeGreaterThanOrEqualTo(1);
    }

    private static async Task LoginOrFailAsync(Browser browser, string userName)
    {
        using var response = await browser.LoginAsync(userName, IdentityDatabaseFixture.Password);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static PasswordHasher<object> Hasher()
    {
        var options = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(options);
        return new PasswordHasher<object>(Options.Create(options));
    }

    private static uint Iterations(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
    }
}
