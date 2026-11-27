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
