using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// User administration (#46, STAGE_5_PLAN section 9) on the real single host and PostgreSQL 17: the API login writes as
/// app_admin for one transaction only, the access changes rotate the security stamp, and administrators cannot lock
/// themselves out.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class UserAdministrationTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Password = "a long initial password";

    private readonly string _tag = "ua" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private HostFactory _host = null!;
    private short _klp;

    /// <summary>A real administrator row: grants record who made them (<c>user_role.granted_by</c>).</summary>
    private TestUser Admin { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _klp = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'KLP'");
        var adminId = await database.CreateUserAsync($"{_tag}.admin", [AppRoles.Administrator], []);
        Admin = new TestUser(adminId, $"{_tag}.admin", IdentityDatabaseFixture.Password, [AppRoles.Administrator], [], null);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_administrator_creates_a_keeper_who_appears_in_the_directory_with_masked_data()
    {
        var id = await CreateAsync("keeper", new UserAccessRequest([AppRoles.WarehouseKeeper], [], _klp));

        var user = await FindAsync("keeper");
        user.Id.ShouldBe(id);
        user.UserName.ShouldBe($"{_tag}.keeper");
        user.Roles.ShouldBe([AppRoles.WarehouseKeeper]);
        user.HomeWarehouseId.ShouldBe(_klp);
        user.IsActive.ShouldBeTrue();
        user.EmailMasked.ShouldNotBe($"{_tag}.keeper@dealer.example", "personal data is masked");
        (await database.ScalarAsync<bool>("SELECT password_hash LIKE 'AQAAAA%' FROM iam.app_user WHERE id = @id", ("id", id))).ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_users_are_refused_with_their_reason()
    {
        await CreateAsync("taken", new UserAccessRequest([AppRoles.Sponsor], [], null));

        using (var taken = await PostAsync(Admin, "/api/v1/identity/users", Request("taken", new UserAccessRequest([AppRoles.Sponsor], [], null))))
        {
            taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(taken)).Code.ShouldBe("Identity.UserNameTaken");
        }

        using (var hub = await PostAsync(Admin, "/api/v1/identity/users", Request("nohub", new UserAccessRequest([AppRoles.WarehouseKeeper], [], 999))))
        {
            hub.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(hub)).Code.ShouldBe("Identity.UnknownWarehouse");
        }

        using (var brand = await PostAsync(Admin, "/api/v1/identity/users", Request("nobrand", new UserAccessRequest([AppRoles.BrandManager], ["NOPE"], null))))
        {
            (await Problem.ReadAsync(brand)).Code.ShouldBe("Identity.UnknownBrand");
        }

        using var weak = await PostAsync(Admin, "/api/v1/identity/users",
            Request("weak", new UserAccessRequest([AppRoles.Sponsor], [], null)) with { Password = "short" });
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Changing_the_access_rotates_the_security_stamp()
    {
        var id = await CreateAsync("manager", new UserAccessRequest([AppRoles.InventoryPlanner], [], null));
        var stamp = await StampAsync(id);

        using var response = await PutAsync(Admin, $"/api/v1/identity/users/{id}/access", new UserAccessRequest([AppRoles.BrandManager], ["CLAAS", "JD"], null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var user = (await response.Content.ReadFromJsonAsync<UserSummary>(Ct))!;
        user.Roles.ShouldBe([AppRoles.BrandManager]);
        user.Brands.ShouldBe(["CLAAS", "JD"]);
        (await StampAsync(id)).ShouldNotBe(stamp, "sessions pick up the new claims at their next check");
    }

    [Fact]
    public async Task Deactivation_unlock_and_anonymisation_follow_the_rules()
    {
        var id = await CreateAsync("leaver", new UserAccessRequest([AppRoles.ServiceEngineer], [], null));
        await database.ExecuteAsync("UPDATE iam.app_user SET lockout_end = now() + interval '1 hour', failed_login_count = 5 WHERE id = @id", ("id", id));
        (await FindAsync("leaver")).IsLockedOut.ShouldBeTrue();

        (await StepAsync(id, "unlock")).IsLockedOut.ShouldBeFalse();
        (await StepAsync(id, "deactivate")).IsActive.ShouldBeFalse();
        (await StepAsync(id, "anonymise")).IsAnonymised.ShouldBeTrue();

        using var after = await PutAsync(Admin, $"/api/v1/identity/users/{id}/access", new UserAccessRequest([AppRoles.Sponsor], [], null));
        after.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(after)).Code.ShouldBe("Identity.Anonymised");
    }

    [Fact]
    public async Task An_administrator_cannot_lock_themselves_out()
    {
        var adminId = await CreateAsync("self", new UserAccessRequest([AppRoles.Administrator], [], null));
        var self = new TestUser(adminId, $"{_tag}.self", Password, [AppRoles.Administrator], [], null);

        using (var deactivate = await PostAsync(self, $"/api/v1/identity/users/{adminId}/deactivate"))
        {
            deactivate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(deactivate)).Code.ShouldBe("Identity.OwnAdministration");
        }

        using (var demote = await PutAsync(self, $"/api/v1/identity/users/{adminId}/access", new UserAccessRequest([AppRoles.Sponsor], [], null)))
        {
            (await Problem.ReadAsync(demote)).Code.ShouldBe("Identity.OwnAdministration");
        }

        using var anonymise = await PostAsync(self, $"/api/v1/identity/users/{adminId}/anonymise");
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "iam.anonymise_user refuses the acting administrator (SS301)");
    }

    [Theory]
    [InlineData(AppRoles.InventoryPlanner)]
    [InlineData(AppRoles.BrandManager)]
    [InlineData(AppRoles.Sponsor)]
    public async Task Only_administrators_manage_users(string role)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync("/api/v1/identity/users", bearer: _host.Token(TestUser.Create("not.admin", [role]).Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_changes_the_own_password_with_the_current_one()
    {
        var id = await CreateAsync("self.service", new UserAccessRequest([AppRoles.ServiceEngineer], [], null));
        var user = new TestUser(id, $"{_tag}.self.service", Password, [AppRoles.ServiceEngineer], [], null);
        var stamp = await StampAsync(id);

        using (var wrong = await PostAsync(user, "/api/v1/identity/me/password", new ChangePasswordRequest("not the password", "a brand new passphrase")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(wrong)).Code.ShouldBe("Identity.WrongPassword");
        }

        using var changed = await PostAsync(user, "/api/v1/identity/me/password", new ChangePasswordRequest(Password, "a brand new passphrase"));
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync(Ct));
        (await StampAsync(id)).ShouldNotBe(stamp);
    }

    [Fact]
    public async Task The_form_offers_roles_brands_and_active_hubs()
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync("/api/v1/identity/users/form-options", bearer: _host.Token(Admin.Principal()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var options = (await response.Content.ReadFromJsonAsync<UserFormOptions>(Ct))!;
        options.Roles.ShouldBe(AppRoles.All, ignoreOrder: true);
        options.Brands.ShouldContain("JD");
        options.Hubs.Count.ShouldBe(7);
    }

    private CreateUserRequest Request(string suffix, UserAccessRequest access) =>
        new($"{_tag}.{suffix}", $"{_tag}.{suffix}@dealer.example", "Test Person", null, Password, access);

    private async Task<Guid> CreateAsync(string suffix, UserAccessRequest access)
    {
        using var response = await PostAsync(Admin, "/api/v1/identity/users", Request(suffix, access));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private async Task<UserSummary> FindAsync(string suffix)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/identity/users?search={_tag}.{suffix}", bearer: _host.Token(Admin.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResponse<UserSummary>>(Ct))!.Items.Single(u => u.UserName == $"{_tag}.{suffix}");
    }

    private async Task<UserSummary> StepAsync(Guid id, string action)
    {
        using var response = await PostAsync(Admin, $"/api/v1/identity/users/{id}/{action}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserSummary>(Ct))!;
    }

    private Task<Guid> StampAsync(Guid id) =>
        database.ScalarAsync<Guid>("SELECT security_stamp FROM iam.app_user WHERE id = @id", ("id", id));

    private async Task<HttpResponseMessage> PostAsync(TestUser user, string path, object? body = null)
    {
        using var browser = _host.NewBrowser();
        return await browser.PostAsync(path, body, bearer: _host.Token(user.Principal()));
    }

    private async Task<HttpResponseMessage> PutAsync(TestUser user, string path, object body)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
        return await browser.Client.SendAsync(request, Ct);
    }

    private sealed record Created(Guid Id);
}
