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
/// User administration on the real single host and PostgreSQL 17 (#46, ADR 0026): accounts are created from employees,
/// their roles come from the employee's position, every change of rights rotates the security stamp, administrative
/// writes run as app_admin for one transaction only, and administrators cannot lock themselves out.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class UserAdministrationTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Password = "a long initial password";

    private readonly string _tag = "ua" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private HostFactory _host = null!;

    /// <summary>A real administrator row: an employee in an administrator position with an account.</summary>
    private TestUser Admin { get; set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var adminId = await database.CreateUserAsync($"{_tag}.admin", [AppRoles.Administrator], []);
        Admin = new TestUser(adminId, $"{_tag}.admin", IdentityDatabaseFixture.Password, [AppRoles.Administrator], [], null);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_account_is_created_from_an_employee_and_gets_the_roles_of_the_position()
    {
        var id = await CreateAsync("keeper", [AppRoles.WarehouseKeeper]);

        var user = await FindAsync("keeper");
        user.Id.ShouldBe(id);
        user.Roles.ShouldBe([AppRoles.WarehouseKeeper]);
        user.Rights.ShouldContain(r => r.Kind == "Role" && r.Value == AppRoles.WarehouseKeeper && r.Origin == "Position");
        user.EmailMasked.ShouldNotBe($"{_tag}.keeper@dealer.example", "personal data is masked");
        (await database.ScalarAsync<bool>("SELECT password_hash LIKE 'AQAAAA%' FROM iam.app_user WHERE id = @id", ("id", id))).ShouldBeTrue();
        (await database.ScalarAsync<bool>("SELECT to_regclass('iam.user_role') IS NULL"))
            .ShouldBeTrue("roles are never stored on the account (ADR 0026)");
    }

    [Fact]
    public async Task Invalid_accounts_and_access_are_refused_with_their_reason()
    {
        var employee = await NewEmployeeAsync("taken", [AppRoles.Sponsor]);
        await AccountAsync(employee, "taken");

        using (var twice = await PostAsync(Admin, $"/api/v1/identity/employees/{employee}/account", new CreateAccountRequest($"{_tag}.other", Password)))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(twice)).Code.ShouldBe("Identity.AccountExists");
        }

        var second = await NewEmployeeAsync("second", [AppRoles.Sponsor]);
        using (var taken = await PostAsync(Admin, $"/api/v1/identity/employees/{second}/account", new CreateAccountRequest($"{_tag}.taken", Password)))
        {
            taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(taken)).Code.ShouldBe("Identity.UserNameTaken");
        }

        using (var weak = await PostAsync(Admin, $"/api/v1/identity/employees/{second}/account", new CreateAccountRequest($"{_tag}.weak", "short")))
        {
            weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task A_new_position_or_a_changed_template_changes_the_roles_and_rotates_the_stamp()
    {
        var user = await CreateAsync("mover", [AppRoles.ServiceEngineer]);
        var employee = await database.ScalarAsync<Guid>("SELECT employee_id FROM iam.app_user WHERE id = @id", ("id", user));
        var stamp = await StampAsync(user);

        var planners = await PositionAsync("planners", [AppRoles.InventoryPlanner]);
        var current = await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{employee}");
        using (var moved = await PutAsync(Admin, $"/api/v1/identity/employees/{employee}",
                   new UpdateEmployeeRequest(current.FullName, current.Email, null, planners.Id, null), current.Version))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        }

        (await FindAsync("mover")).Roles.ShouldBe([AppRoles.InventoryPlanner]);
        var afterMove = await StampAsync(user);
        afterMove.ShouldNotBe(stamp);

        var template = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{planners.Id}");
        using (var changed = await PutAsync(Admin, $"/api/v1/identity/positions/{planners.Id}",
                   new UpdatePositionRequest(template.Name, null, [AppRoles.InventoryPlanner, AppRoles.Sponsor]), template.Version))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        }

        (await FindAsync("mover")).Roles.ShouldBe([AppRoles.InventoryPlanner, AppRoles.Sponsor]);
        (await StampAsync(user)).ShouldNotBe(afterMove, "a template change reaches every holder of the position");

        using var stale = await PutAsync(Admin, $"/api/v1/identity/positions/{planners.Id}",
            new UpdatePositionRequest("Stale write", null, [AppRoles.InventoryPlanner]), template.Version);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the version read before the change is stale");
    }

    [Fact]
    public async Task An_employee_who_left_has_no_rights_and_a_position_in_use_cannot_be_archived()
    {
        var user = await CreateAsync("leaver", [AppRoles.WarehouseKeeper]);
        var employee = await database.ScalarAsync<Guid>("SELECT employee_id FROM iam.app_user WHERE id = @id", ("id", user));
        var position = await database.ScalarAsync<short>("SELECT position_id FROM iam.employee WHERE id = @id", ("id", employee));
        var positionVersion = (await GetAsync<PositionSummary>($"/api/v1/identity/positions/{position}")).Version;

        using (var inUse = await PostAsync(Admin, $"/api/v1/identity/positions/{position}/archive", null, positionVersion))
        {
            inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(inUse)).Code.ShouldBe("SS501");
        }

        var stamp = await StampAsync(user);
        var version = (await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{employee}")).Version;
        using (var left = await PostAsync(Admin, $"/api/v1/identity/employees/{employee}/archive", new ArchiveEmployeeRequest(new DateOnly(2026, 12, 31)), version))
        {
            left.StatusCode.ShouldBe(HttpStatusCode.OK, await left.Content.ReadAsStringAsync(Ct));
        }

        (await StampAsync(user)).ShouldNotBe(stamp);
        var after = await FindAsync("leaver");
        after.Rights.ShouldBeEmpty();
        after.InactiveReason.ShouldNotBeNull();

        using var noVersion = await PostAsync(Admin, $"/api/v1/identity/positions/{position}/archive");
        (await Problem.ReadAsync(noVersion)).Code.ShouldBe("Identity.VersionRequired");
    }

    [Fact]
    public async Task Deactivation_unlock_and_anonymisation_follow_the_rules()
    {
        var id = await CreateAsync("locked", [AppRoles.ServiceEngineer]);
        await database.ExecuteAsync("UPDATE iam.app_user SET lockout_end = now() + interval '1 hour', failed_login_count = 5 WHERE id = @id", ("id", id));
        (await FindAsync("locked")).IsLockedOut.ShouldBeTrue();

        (await StepAsync(id, "unlock")).IsLockedOut.ShouldBeFalse();
        (await StepAsync(id, "deactivate")).IsActive.ShouldBeFalse();
        (await StepAsync(id, "anonymise")).IsAnonymised.ShouldBeTrue();
        (await database.ScalarAsync<bool>(
            "SELECT e.anonymised_at IS NOT NULL AND e.email IS NULL AND e.full_name = 'Anonymised Employee' FROM iam.employee e JOIN iam.app_user u ON u.employee_id = e.id WHERE u.id = @id",
            ("id", id))).ShouldBeTrue("anonymising an account erases its employee too");

        using var after = await PostAsync(Admin, $"/api/v1/identity/users/{id}/unlock");
        after.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(after)).Code.ShouldBe("Identity.Anonymised");
    }

    [Fact]
    public async Task An_administrator_cannot_lock_themselves_out()
    {
        var adminId = await CreateAsync("self", [AppRoles.Administrator]);
        var self = new TestUser(adminId, $"{_tag}.self", Password, [AppRoles.Administrator], [], null);
        var employee = await database.ScalarAsync<Guid>("SELECT employee_id FROM iam.app_user WHERE id = @id", ("id", adminId));

        using (var deactivate = await PostAsync(self, $"/api/v1/identity/users/{adminId}/deactivate"))
        {
            deactivate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(deactivate)).Code.ShouldBe("Identity.OwnAdministration");
        }

        var version = (await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{employee}")).Version;
        using (var leave = await PostAsync(self, $"/api/v1/identity/employees/{employee}/archive", new ArchiveEmployeeRequest(new DateOnly(2026, 12, 31)), version))
        {
            (await Problem.ReadAsync(leave)).Code.ShouldBe("Identity.OwnAdministration");
        }

        using var anonymise = await PostAsync(self, $"/api/v1/identity/users/{adminId}/anonymise");
        anonymise.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "iam.anonymise_employee refuses the acting administrator (SS301)");
    }

    [Theory]
    [InlineData(AppRoles.InventoryPlanner)]
    [InlineData(AppRoles.BrandManager)]
    [InlineData(AppRoles.Sponsor)]
    public async Task Only_administrators_manage_users_employees_and_positions(string role)
    {
        using var browser = _host.NewBrowser();
        var token = _host.Token(TestUser.Create("not.admin", [role]).Principal());
        foreach (var path in new[] { "/api/v1/identity/users", "/api/v1/identity/employees", "/api/v1/identity/positions" })
        {
            using var response = await browser.GetAsync(path, bearer: token);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task No_endpoint_creates_a_user_without_an_employee()
    {
        using var response = await PostAsync(Admin, "/api/v1/identity/users", new { userName = $"{_tag}.ghost", password = Password });

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_user_changes_the_own_password_with_the_current_one()
    {
        var id = await CreateAsync("self.service", [AppRoles.ServiceEngineer]);
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
        var options = await GetAsync<UserFormOptions>("/api/v1/identity/users/form-options");

        AppRoles.All.ShouldAllBe(r => options.Roles.Contains(r), "roles are data (ADR 0028): the seeded ones and any created since");
        options.Brands.ShouldContain("JD");
        options.Hubs.Count.ShouldBe(7);
    }

    /// <summary>A position with the given template, an employee in it, and the employee's account.</summary>
    private async Task<Guid> CreateAsync(string suffix, string[] roles)
    {
        var employee = await NewEmployeeAsync(suffix, roles);
        await AccountAsync(employee, suffix);
        return (await FindAsync(suffix)).Id;
    }

    private async Task<Guid> NewEmployeeAsync(string suffix, string[] roles)
    {
        var position = await PositionAsync(suffix, roles);
        var code = "E" + RandomNumberGenerator.GetInt32(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var response = await PostAsync(Admin, "/api/v1/identity/employees",
            new CreateEmployeeRequest(code, "Test Person", $"{_tag}.{suffix}@dealer.example", null, position.Id, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<EmployeeSummary>(Ct))!.Id;
    }

    private async Task AccountAsync(Guid employee, string suffix)
    {
        using var response = await PostAsync(Admin, $"/api/v1/identity/employees/{employee}/account", new CreateAccountRequest($"{_tag}.{suffix}", Password));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<PositionSummary> PositionAsync(string suffix, string[] roles)
    {
        var code = $"{_tag}_{suffix}".ToUpperInvariant().Replace('.', '_');
        using var response = await PostAsync(Admin, "/api/v1/identity/positions", new CreatePositionRequest(code, $"Position {suffix}", null, roles));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PositionSummary>(Ct))!;
    }

    private async Task<UserSummary> FindAsync(string suffix)
    {
        var page = await GetAsync<PagedResponse<UserSummary>>($"/api/v1/identity/users?search={_tag}.{suffix}");
        return page.Items.Single(u => u.UserName == $"{_tag}.{suffix}");
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: _host.Token(Admin.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private async Task<UserSummary> StepAsync(Guid id, string action)
    {
        using var response = await PostAsync(Admin, $"/api/v1/identity/users/{id}/{action}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserSummary>(Ct))!;
    }

    private Task<Guid> StampAsync(Guid id) =>
        database.ScalarAsync<Guid>("SELECT security_stamp FROM iam.app_user WHERE id = @id", ("id", id));

    private async Task<HttpResponseMessage> PostAsync(TestUser user, string path, object? body = null, string? ifMatch = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> PutAsync(TestUser user, string path, object body, string? ifMatch = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }
}
