using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Roles as data and the guardrails of ADR 0028 (PR 7x3a): the locked Administrator role, separation of duties for roles
/// and position templates, no self-granting (own role, own position, own template, own assignments), archive rules and
/// the stamp rotation that refreshes sessions. Single host, PostgreSQL 17 as <c>smartspare_api</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class RoleApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Roles = "/api/v1/identity/roles";

    /// <summary>Letters only: role codes allow capital letters and underscores.</summary>
    private readonly string _tag = "R" + new string([.. RandomNumberGenerator.GetBytes(5).Select(b => (char)('A' + (b % 26)))]);

    private HostFactory _host = null!;
    private TestUser _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var id = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.admin", [AppRoles.Administrator], []);
        _admin = new TestUser(id, $"{_tag.ToLowerInvariant()}.admin", IdentityDatabaseFixture.Password, [AppRoles.Administrator], []);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_administrator_role_is_listed_locked_and_cannot_be_changed()
    {
        var roles = await GetAsync<List<RoleSummary>>(Roles);
        var administrator = roles.Single(r => r.Code == AppRoles.Administrator);
        administrator.IsLocked.ShouldBeTrue();
        administrator.Permissions.ShouldContain(Permissions.Organisation.Role.Edit);
        administrator.Permissions.ShouldContain(Permissions.Procurement.Supplier.Edit, "supplier maintenance is the administrator's by default");

        using var refused = await PutAsync(administrator.Id, new UpdateRoleRequest("Administrator", null, []), administrator.Version, _admin);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("Identity.RoleLocked");
    }

    [Fact]
    public async Task A_role_is_created_once_and_never_breaks_separation_of_duties()
    {
        using var browser = _host.NewBrowser();
        using (var created = await browser.PostAsync(Roles, new CreateRoleRequest($"{_tag}_CLERK", "Clerk", null, [Permissions.Inventory.Part.View]), bearer: Token(_admin)))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        }

        using (var duplicate = await browser.PostAsync(Roles, new CreateRoleRequest($"{_tag}_CLERK", "Again", null, []), bearer: Token(_admin)))
        {
            duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using var conflict = await browser.PostAsync(Roles,
            new CreateRoleRequest($"{_tag}_BUYER", "Buyer", null, [Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.Supplier.Edit]), bearer: Token(_admin));
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await Problem.ReadAsync(conflict);
        problem.Code.ShouldBe("Identity.DutyConflict");
        problem.Detail!.ShouldContain(Permissions.Procurement.Supplier.Edit);
    }

    [Fact]
    public async Task A_position_template_must_not_combine_conflicting_roles()
    {
        var supplier = await CreateRoleAsync("_SUPPLIER", [Permissions.Procurement.Supplier.Edit]);
        using var browser = _host.NewBrowser();

        using var refused = await browser.PostAsync("/api/v1/identity/positions",
            new CreatePositionRequest($"{_tag}_BUYER", "Buyer", null, [AppRoles.BrandManager, supplier.Code]), bearer: Token(_admin));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, "brand managers create purchase orders");
        (await Problem.ReadAsync(refused)).Code.ShouldBe("Identity.DutyConflict");

        // The other way round: a role that a position already holds may not gain the conflicting permission.
        var helper = await CreateRoleAsync("_HELPER", [Permissions.Inventory.Part.View]);
        using (var position = await browser.PostAsync("/api/v1/identity/positions",
                   new CreatePositionRequest($"{_tag}_MIXED", "Mixed", null, [AppRoles.BrandManager, helper.Code]), bearer: Token(_admin)))
        {
            position.StatusCode.ShouldBe(HttpStatusCode.Created, await position.Content.ReadAsStringAsync(Ct));
        }

        using var widened = await PutAsync(helper.Id, new UpdateRoleRequest("Helper", null, [Permissions.Procurement.Supplier.Edit]), helper.Version, _admin);
        widened.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(widened)).Code.ShouldBe("Identity.DutyConflict");
    }

    [Fact]
    public async Task Nobody_changes_a_role_they_hold()
    {
        var role = await CreateRoleAsync("_OWN", [Permissions.Inventory.Part.View]);
        var holder = new TestUser(_admin.Id, _admin.UserName, IdentityDatabaseFixture.Password, [AppRoles.Administrator, role.Code], []);

        using var refused = await PutAsync(role.Id, new UpdateRoleRequest("Own", null, [Permissions.Inventory.Stock.View]), role.Version, holder);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("SS301");
    }

    [Fact]
    public async Task A_permission_change_rotates_the_stamps_of_everyone_who_holds_the_role()
    {
        var role = await CreateRoleAsync("_ROTATE", [Permissions.Inventory.Part.View]);
        var holder = await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.holder", [role.Code], []);
        var before = await StampAsync(holder);

        using var changed = await PutAsync(role.Id, new UpdateRoleRequest("Rotate", null, [Permissions.Inventory.Part.View, Permissions.Inventory.Stock.View]), role.Version, _admin);

        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        (await StampAsync(holder)).ShouldNotBe(before, "the session gets the new permissions at the next check");
        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE table_name = 'iam.role_permission' AND new_values ->> 'permission' = 'Inventory.Stock.View'"))
            .ShouldBeGreaterThan(0, "the change is audited");
    }

    [Fact]
    public async Task A_role_edit_ends_the_holders_sessions_within_30_seconds_and_the_next_sign_in_has_the_new_bundle()
    {
        var role = await CreateRoleAsync("_LIVE", [Permissions.Inventory.Part.View]);
        var userName = $"{_tag.ToLowerInvariant()}.live";
        var id = await database.CreateUserAsync(userName, [role.Code], []);
        _host.Time.Advance(TimeSpan.FromSeconds(30)); // a new role, like any change, reaches the cached map within the window
        using var session = _host.NewBrowser();
        await session.LoginOrFailAsync(new TestUser(id, userName, IdentityDatabaseFixture.Password, [role.Code], []));
        (await session.MeAsync()).Permissions.ShouldBe([Permissions.Inventory.Part.View]);

        using (var changed = await PutAsync(role.Id, new UpdateRoleRequest("Live", null, [Permissions.Inventory.Part.View, Permissions.Inventory.Stock.View]), role.Version, _admin))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        }

        _host.Time.Advance(TimeSpan.FromSeconds(30));

        (await session.MeAsync()).IsAuthenticated.ShouldBeFalse("the edit rotates the holders' stamps, so the session check ends the session within 30 seconds");
        using var again = _host.NewBrowser();
        await again.LoginOrFailAsync(new TestUser(id, userName, IdentityDatabaseFixture.Password, [role.Code], []));
        (await again.MeAsync()).Permissions.ShouldBe([Permissions.Inventory.Part.View, Permissions.Inventory.Stock.View], ignoreOrder: true,
            "the next sign-in carries the edited bundle (ADR 0028)");
    }

    [Fact]
    public async Task A_role_in_a_template_cannot_be_archived_and_an_archived_role_cannot_be_chosen()
    {
        var used = await CreateRoleAsync("_USED", [Permissions.Inventory.Part.View]);
        await database.CreateUserAsync($"{_tag.ToLowerInvariant()}.user", [used.Code], []);
        using (var inUse = await StateAsync(used.Id, "archive", used.Version))
        {
            inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(inUse)).Code.ShouldBe("SS501");
        }

        var unused = await CreateRoleAsync("_UNUSED", []);
        using (var archived = await StateAsync(unused.Id, "archive", unused.Version))
        {
            archived.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var browser = _host.NewBrowser();
        using var refused = await browser.PostAsync("/api/v1/identity/positions",
            new CreatePositionRequest($"{_tag}_OLD", "Old", null, [unused.Code]), bearer: Token(_admin));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("SS500");
    }

    [Fact]
    public async Task Nobody_changes_their_own_position_template_position_or_assignments()
    {
        var positionId = await database.ScalarAsync<short>("SELECT position_id FROM iam.employee WHERE id = @id", ("id", _admin.Id));
        var position = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{positionId}");
        using var browser = _host.NewBrowser();

        using (var template = await SendAsync(HttpMethod.Put, $"/api/v1/identity/positions/{positionId}",
                   new UpdatePositionRequest(position.Name, null, [.. position.Roles]), position.Version))
        {
            template.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "own position's template");
        }

        var me = await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{_admin.Id}");
        var other = await database.ScalarAsync<short>("SELECT id FROM iam.position WHERE code = 'INVENTORY_PLANNER'");
        using (var move = await SendAsync(HttpMethod.Put, $"/api/v1/identity/employees/{_admin.Id}",
                   new UpdateEmployeeRequest(me.FullName, me.Email, me.Phone, other, me.HiredOn), me.Version))
        {
            move.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "own position");
        }

        using var assignment = await browser.PutAsync("/api/v1/inventory/warehouses/1/responsibles",
            new ResponsiblesRequest([new ResponsibleEntry(_admin.Id, "KEEPER")]), bearer: Token(_admin));
        assignment.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "own hub assignment");
        (await Problem.ReadAsync(assignment)).Code.ShouldBe("SS301");
    }

    [Fact]
    public async Task A_seeded_role_is_renamed_and_edited_and_reset_to_default_restores_its_shipped_permissions_audited()
    {
        var sponsor = (await GetAsync<List<RoleSummary>>(Roles)).Single(r => r.Code == AppRoles.Sponsor);
        (sponsor.HasDefault, sponsor.IsDefault, sponsor.IsLocked).ShouldBe((true, true, false), "seeded roles are editable and ship at their default");
        var shipped = DefaultRoles.Bundles[AppRoles.Sponsor];
        var trimmed = shipped.Where(p => p != Permissions.Inventory.Stock.View).Order(StringComparer.Ordinal).ToList();

        try
        {
            using (var edited = await PutAsync(sponsor.Id, new UpdateRoleRequest("Sponsor (edited)", sponsor.Description, trimmed), sponsor.Version, _admin))
            {
                edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
                var role = (await edited.Content.ReadFromJsonAsync<RoleSummary>(Ct))!;
                (role.Name, role.HasDefault, role.IsDefault).ShouldBe(("Sponsor (edited)", true, false));
                sponsor = role;
            }

            using var reset = await StateAsync(sponsor.Id, "reset", sponsor.Version);
            reset.StatusCode.ShouldBe(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync(Ct));
            var restored = (await reset.Content.ReadFromJsonAsync<RoleSummary>(Ct))!;
            restored.Permissions.ShouldBe([.. shipped.Order(StringComparer.Ordinal)], "the shipped permission set is back");
            (restored.Name, restored.IsDefault).ShouldBe(("Sponsor (edited)", true), "reset restores the permissions only; the name stays");
            (await database.ScalarAsync<long>(
                    "SELECT count(*) FROM audit.audit_log WHERE table_name = 'iam.role_permission' AND action = 'INSERT' "
                    + "AND new_values ->> 'permission' = 'Inventory.Stock.View' AND (new_values ->> 'role_id')::smallint = @role",
                    ("role", sponsor.Id)))
                .ShouldBeGreaterThan(0, "the reset is audited like any permission change");
        }
        finally
        {
            var current = (await GetAsync<List<RoleSummary>>(Roles)).Single(r => r.Code == AppRoles.Sponsor);
            using var back = await PutAsync(current.Id, new UpdateRoleRequest("Sponsor", current.Description, [.. shipped]), current.Version, _admin);
        }
    }

    [Fact]
    public async Task An_upgrade_grant_to_a_customised_seeded_role_is_audited_as_the_system_user_and_marked_until_confirmed()
    {
        const string Version = "v9.9.9-test";
        var roles = await GetAsync<List<RoleSummary>>(Roles);
        var sponsor = roles.Single(r => r.Code == AppRoles.Sponsor);
        var shipped = DefaultRoles.Bundles[AppRoles.Sponsor];
        try
        {
            using (var edited = await PutAsync(sponsor.Id, new UpdateRoleRequest(sponsor.Name, sponsor.Description,
                       [.. shipped.Where(p => p != Permissions.Inventory.Stock.View)]), sponsor.Version, _admin))
            {
                edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Ct));
            }

            (await database.ScalarAsync<bool>("SELECT is_customised FROM iam.role WHERE code = 'SPONSOR'")).ShouldBeTrue("an edit away from the shipped set");

            // What a later permission migration does; an unchanged seeded role only gains the permission.
            await database.ExecuteAsync(
                "SELECT iam.grant_default_permission('SPONSOR', 'Inventory.Stock.View', @v); SELECT iam.grant_default_permission('BRAND_MANAGER', 'Audit.Log.View', @v);",
                ("v", Version));

            (await database.ScalarAsync<long>(
                    "SELECT count(*) FROM audit.audit_log WHERE table_name = 'iam.role_permission' AND action = 'INSERT' "
                    + "AND new_values ->> 'permission' = 'Inventory.Stock.View' AND user_id = '00000000-0000-4000-8000-00000000f001' AND reason = @reason",
                    ("reason", "granted by upgrade " + Version)))
                .ShouldBe(1, "the grant is audited as the system user with the upgrade version");
            (await database.ScalarAsync<string>("SELECT coalesce(upgrade_pending, '') FROM iam.role WHERE code = 'BRAND_MANAGER'")).ShouldBeEmpty("the brand manager role was not customised");

            var marked = (await GetAsync<List<RoleSummary>>(Roles)).Single(r => r.Code == AppRoles.Sponsor);
            marked.UpgradePending.ShouldBe(Version);

            using (var confirmed = await StateAsync(marked.Id, "confirm-upgrade", marked.Version))
            {
                confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync(Ct));
                var role = (await confirmed.Content.ReadFromJsonAsync<RoleSummary>(Ct))!;
                role.UpgradePending.ShouldBeNull();
                using var again = await StateAsync(role.Id, "confirm-upgrade", role.Version);
                again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await Problem.ReadAsync(again)).Code.ShouldBe("Identity.NoUpgradePending");
            }
        }
        finally
        {
            await database.ExecuteAsync(
                "DELETE FROM iam.role_permission rp USING iam.role r WHERE rp.role_id = r.id AND r.code = 'BRAND_MANAGER' AND rp.permission = 'Audit.Log.View'");
            var current = (await GetAsync<List<RoleSummary>>(Roles)).Single(r => r.Code == AppRoles.Sponsor);
            using var back = await StateAsync(current.Id, "reset", current.Version);
        }

        (await database.ScalarAsync<bool>("SELECT is_customised FROM iam.role WHERE code = 'SPONSOR'")).ShouldBeFalse("reset to default ends the customisation");
    }

    [Fact]
    public async Task Reset_to_default_is_refused_for_a_new_role_a_locked_role_and_a_role_one_holds()
    {
        var custom = await CreateRoleAsync("_NODEFAULT", [Permissions.Inventory.Part.View]);
        using (var noDefault = await StateAsync(custom.Id, "reset", custom.Version))
        {
            noDefault.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(noDefault)).Code.ShouldBe("Identity.NoDefaultRole");
        }

        var roles = await GetAsync<List<RoleSummary>>(Roles);
        var administrator = roles.Single(r => r.Code == AppRoles.Administrator);
        using (var locked = await StateAsync(administrator.Id, "reset", administrator.Version))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(locked)).Code.ShouldBe("Identity.RoleLocked");
        }

        var planner = roles.Single(r => r.Code == AppRoles.InventoryPlanner);
        var holder = new TestUser(_admin.Id, _admin.UserName, IdentityDatabaseFixture.Password, [AppRoles.Administrator, AppRoles.InventoryPlanner], []);
        using var own = await SendAsync(HttpMethod.Post, $"{Roles}/{planner.Id}/reset", null, planner.Version, holder);
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Problem.ReadAsync(own)).Code.ShouldBe("SS301");
    }

    [Fact]
    public async Task A_copy_of_the_locked_administrator_role_is_an_editable_role_and_the_superadministrator_is_not_copied()
    {
        var roles = await GetAsync<List<RoleSummary>>(Roles);
        var administrator = roles.Single(r => r.Code == AppRoles.Administrator);
        using var browser = _host.NewBrowser();

        using (var copied = await browser.PostAsync($"{Roles}/{administrator.Id}/copy", new CopyRoleRequest($"{_tag}_ADMIN", "Deputy administrator"), bearer: Token(_admin)))
        {
            copied.StatusCode.ShouldBe(HttpStatusCode.Created, await copied.Content.ReadAsStringAsync(Ct));
            var copy = (await copied.Content.ReadFromJsonAsync<RoleSummary>(Ct))!;
            (copy.IsLocked, copy.HasDefault, copy.Name).ShouldBe((false, false, "Deputy administrator"));
            copy.Permissions.ShouldBe(administrator.Permissions);

            using var trimmed = await PutAsync(copy.Id, new UpdateRoleRequest(copy.Name, null, [Permissions.Organisation.Role.View]), copy.Version, _admin);
            trimmed.StatusCode.ShouldBe(HttpStatusCode.OK, "the copy is editable");
        }

        using (var again = await browser.PostAsync($"{Roles}/{administrator.Id}/copy", new CopyRoleRequest($"{_tag}_ADMIN", "Twice"), bearer: Token(_admin)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the code of a copy must be new");
        }

        var superadministrator = roles.Single(r => r.Code == AppRoles.Superadministrator);
        using var refused = await browser.PostAsync($"{Roles}/{superadministrator.Id}/copy", new CopyRoleRequest($"{_tag}_ALL", "Everything"), bearer: Token(_admin));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("Identity.SuperadministratorRole");
    }

    [Fact]
    public async Task A_renamed_position_with_a_changed_template_refreshes_its_employees_sessions_within_30_seconds()
    {
        var userName = $"{_tag.ToLowerInvariant()}.moved";
        var id = await database.CreateUserAsync(userName, [AppRoles.ServiceEngineer], []);
        var positionId = await database.ScalarAsync<short>("SELECT position_id FROM iam.employee WHERE id = @id", ("id", id));
        var position = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{positionId}");
        using var session = _host.NewBrowser();
        await session.LoginOrFailAsync(new TestUser(id, userName, IdentityDatabaseFixture.Password, [AppRoles.ServiceEngineer], []));

        using (var changed = await SendAsync(HttpMethod.Put, $"/api/v1/identity/positions/{positionId}",
                   new UpdatePositionRequest("Senior engineer", null, [AppRoles.ServiceEngineer, AppRoles.WarehouseKeeper]), position.Version))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
            (await changed.Content.ReadFromJsonAsync<PositionSummary>(Ct))!.Name.ShouldBe("Senior engineer");
        }

        _host.Time.Advance(TimeSpan.FromSeconds(30));

        (await session.MeAsync()).IsAuthenticated.ShouldBeFalse("a template change rotates the employees' stamps, so the session check ends the session within 30 seconds");
        using var again = _host.NewBrowser();
        await again.LoginOrFailAsync(new TestUser(id, userName, IdentityDatabaseFixture.Password, [AppRoles.ServiceEngineer, AppRoles.WarehouseKeeper], []));
        (await again.MeAsync()).Roles.ShouldBe([AppRoles.ServiceEngineer, AppRoles.WarehouseKeeper], ignoreOrder: true, "the next sign-in carries the new template");
    }

    [Fact]
    public async Task The_locked_superadministrator_position_is_listed_locked_and_cannot_be_renamed()
    {
        var positionId = await database.ScalarAsync<short>("SELECT id FROM iam.position WHERE code = 'SUPERADMINISTRATOR'");
        var position = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{positionId}");
        position.IsLocked.ShouldBeTrue();

        using var refused = await SendAsync(HttpMethod.Put, $"/api/v1/identity/positions/{positionId}", new UpdatePositionRequest("Renamed", null, []), position.Version);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBe("Identity.PositionLocked");
    }

    private async Task<RoleSummary> CreateRoleAsync(string suffix, string[] permissions)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.PostAsync(Roles, new CreateRoleRequest(_tag + suffix, suffix.Trim('_'), null, permissions), bearer: Token(_admin));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RoleSummary>(Ct))!;
    }

    private Task<HttpResponseMessage> PutAsync(short id, UpdateRoleRequest body, string version, TestUser user) =>
        SendAsync(HttpMethod.Put, $"{Roles}/{id}", body, version, user);

    private Task<HttpResponseMessage> StateAsync(short id, string action, string version) =>
        SendAsync(HttpMethod.Post, $"{Roles}/{id}/{action}", null, version);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string version, TestUser? user = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user ?? _admin));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(_admin));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private Task<string> StampAsync(Guid userId) =>
        database.ScalarAsync<string>("SELECT security_stamp::text FROM iam.app_user WHERE id = @id", ("id", userId));

    private string Token(TestUser user) => _host.Token(user.Principal());
}
