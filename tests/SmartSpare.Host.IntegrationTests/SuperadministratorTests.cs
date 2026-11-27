using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The Superadministrator (ADR 0028, PR 7x3b): a locked position and role that nobody edits, archives or reuses;
/// only a superadministrator moves someone into or out of the position, never themselves; the last active one stays.
/// Every permission and no scope limit are checked by the access matrix and T03, the invariants and four-eyes by T03.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class SuperadministratorTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private readonly string _tag = "s" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private HostFactory _host = null!;
    private TestUser _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var id = await database.CreateUserAsync($"{_tag}.admin", [AppRoles.Administrator], []);
        _admin = new TestUser(id, $"{_tag}.admin", IdentityDatabaseFixture.Password, [AppRoles.Administrator], []);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_position_and_role_are_locked_and_the_role_belongs_to_no_other_template()
    {
        var position = await GetAsync<PositionSummary>($"/api/v1/identity/positions/{await SuperPositionIdAsync()}", _admin);
        using (var edit = await SendAsync(HttpMethod.Put, $"/api/v1/identity/positions/{position.Id}", new UpdatePositionRequest("Renamed", null, []), position.Version, _admin))
        {
            edit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(edit)).Code.ShouldBe("Identity.PositionLocked");
        }

        using (var archive = await SendAsync(HttpMethod.Post, $"/api/v1/identity/positions/{position.Id}/archive", null, position.Version, _admin))
        {
            (await Problem.ReadAsync(archive)).Code.ShouldBe("Identity.PositionLocked");
        }

        var role = (await GetAsync<List<RoleSummary>>("/api/v1/identity/roles", _admin)).Single(r => r.Code == AppRoles.Superadministrator);
        role.IsLocked.ShouldBeTrue();
        role.Permissions.Count.ShouldBe(Permissions.All.Count, "every permission");
        using (var roleEdit = await SendAsync(HttpMethod.Put, $"/api/v1/identity/roles/{role.Id}", new UpdateRoleRequest("Less", null, []), role.Version, _admin))
        {
            (await Problem.ReadAsync(roleEdit)).Code.ShouldBe("Identity.RoleLocked");
        }

        using var browser = _host.NewBrowser();
        using var reuse = await browser.PostAsync("/api/v1/identity/positions",
            new CreatePositionRequest($"{_tag.ToUpperInvariant()}_SHADOW", "Shadow", null, [AppRoles.Superadministrator]), bearer: Token(_admin));
        reuse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(reuse)).Code.ShouldBe("Identity.SuperadministratorRole");
    }

    [Fact]
    public async Task Only_a_superadministrator_moves_someone_into_or_out_of_the_position_and_never_themselves()
    {
        var super = await SuperadministratorAsync("first");
        var employee = await database.CreateUserAsync($"{_tag}.staff", [AppRoles.InventoryPlanner], []);
        var superPosition = await SuperPositionIdAsync();

        using (var byAdmin = await MoveAsync(employee, superPosition, _admin))
        {
            byAdmin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Problem.ReadAsync(byAdmin)).Code.ShouldBe("Identity.SuperadministratorOnly");
        }

        using (var bySuper = await MoveAsync(employee, superPosition, super))
        {
            bySuper.StatusCode.ShouldBe(HttpStatusCode.OK, await bySuper.Content.ReadAsStringAsync(Ct));
        }

        var planner = await database.ScalarAsync<short>("SELECT id FROM iam.position WHERE code = 'INVENTORY_PLANNER'");
        using (var outBySuper = await MoveAsync(employee, planner, super))
        {
            outBySuper.StatusCode.ShouldBe(HttpStatusCode.OK, "another superadministrator moves them out again");
        }

        using var self = await MoveAsync(super.Id, planner, super);
        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "never oneself");
        (await Problem.ReadAsync(self)).Code.ShouldBe("SS301");
    }

    [Fact]
    public async Task The_last_active_superadministrator_cannot_be_removed()
    {
        var last = await SuperadministratorAsync("last");
        await database.ExecuteAsync(
            """
            UPDATE iam.employee SET is_archived = true, archived_at = now(), left_on = current_date
            WHERE id <> @id AND position_id = (SELECT id FROM iam.position WHERE code = 'SUPERADMINISTRATOR') AND NOT is_archived
            """,
            ("id", last.Id));
        using var browser = _host.NewBrowser();

        using (var deactivate = await browser.PostAsync($"/api/v1/identity/users/{last.Id}/deactivate", new { }, bearer: Token(_admin)))
        {
            deactivate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(deactivate)).Code.ShouldBe("Identity.LastSuperadministrator");
        }

        var employee = await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{last.Id}", _admin);
        using var archive = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{last.Id}/archive",
            new ArchiveEmployeeRequest(DateOnly.FromDateTime(DateTime.UtcNow)), employee.Version, _admin);
        archive.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(archive)).Code.ShouldBe("Identity.LastSuperadministrator");
    }

    /// <summary>An employee in the Superadministrator position with an account, acting with that role.</summary>
    private async Task<TestUser> SuperadministratorAsync(string suffix)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync(
            $"""
            INSERT INTO iam.employee (id, code, full_name, position_id)
            SELECT @id, 'E' || lpad((coalesce((SELECT max(substr(code, 2)::int) FROM iam.employee), 0) + 1)::text, 6, '0'), 'Super Test', p.id
            FROM iam.position p WHERE p.code = 'SUPERADMINISTRATOR';
            INSERT INTO iam.app_user (id, employee_id, user_name, password_hash) VALUES (@id, @id, @name, 'AQAAAAIAA1tgAAAAE{new string('A', 67)}');
            """,
            ("id", id), ("name", $"{_tag}.{suffix}"));
        return new TestUser(id, $"{_tag}.{suffix}", IdentityDatabaseFixture.Password, [AppRoles.Superadministrator], []);
    }

    private async Task<HttpResponseMessage> MoveAsync(Guid employeeId, short positionId, TestUser actor)
    {
        var employee = await GetAsync<EmployeeSummary>($"/api/v1/identity/employees/{employeeId}", _admin);
        return await SendAsync(HttpMethod.Put, $"/api/v1/identity/employees/{employeeId}",
            new UpdateEmployeeRequest(employee.FullName, employee.Email, employee.Phone, positionId, employee.HiredOn), employee.Version, actor);
    }

    private Task<short> SuperPositionIdAsync() =>
        database.ScalarAsync<short>("SELECT id FROM iam.position WHERE code = 'SUPERADMINISTRATOR'");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string version, TestUser user)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<T> GetAsync<T>(string path, TestUser user)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(user));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private string Token(TestUser user) => _host.Token(user.Principal());
}
