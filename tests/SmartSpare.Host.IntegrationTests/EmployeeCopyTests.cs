using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// ADR 0029 amendment 2 (PR 7j1c): Inventory keeps a read-only copy of Identity's employees, fed by
/// <c>EmployeeDirectoryChanged</c> with every employee change and filled by <c>identity.employee-publish</c>, the newest
/// event winning; assignments take only active employees from it; anonymisation reaches the copy and redacts the name
/// in Identity's outbox. Single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class EmployeeCopyTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Admin = TestUser.Create("copy.admin", [AppRoles.Administrator]);

    private readonly string _name = "Copy Tester " + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Every_employee_change_reaches_the_copy_and_anonymisation_also_redacts_Identitys_outbox()
    {
        var employee = await CreateEmployeeAsync();
        await EventuallyAsync(async () => await CopyAsync(employee) is ({ } name, true, null) && name == _name);

        using (var account = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{employee}/account",
                   new CreateAccountRequest($"copy.{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}", "Correct-Horse-Battery-9")))
        {
            account.StatusCode.ShouldBe(HttpStatusCode.Created, await account.Content.ReadAsStringAsync(Ct));
        }

        await EventuallyAsync(async () => (await CopyAsync(employee)).AccountId is not null);

        using (var anonymise = await SendAsync(HttpMethod.Post, $"/api/v1/identity/employees/{employee}/anonymise", null))
        {
            anonymise.StatusCode.ShouldBe(HttpStatusCode.OK, await anonymise.Content.ReadAsStringAsync(Ct));
        }

        await EventuallyAsync(async () => await CopyAsync(employee) is ("Anonymised Employee", false, _));
        (await database.ScalarAsync<long>(
            """
            SELECT count(*) FROM iam.outbox_message
            WHERE type LIKE 'SmartSpare.Modules.Identity.Contracts.EmployeeDirectoryChanged,%' AND payload::text LIKE '%' || @name || '%'
            """, ("name", _name))).ShouldBe(0, "no Identity payload keeps the anonymised employee's name");
        (await database.ScalarAsync<long>(
            """
            SELECT count(*) FROM iam.outbox_message
            WHERE type LIKE 'SmartSpare.Modules.Identity.Contracts.EmployeeDirectoryChanged,%'
              AND payload->'employees' @> jsonb_build_array(jsonb_build_object('employeeId', @id::text))
            """, ("id", employee))).ShouldBeGreaterThan(1, "the earlier rows stay for tracing, redacted");
    }

    [Fact]
    public async Task The_publish_job_refills_the_copy_and_an_older_event_never_overwrites_a_newer_one()
    {
        var employee = await database.CreateUserAsync($"copy.job.{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}", [AppRoles.WarehouseKeeper], []);
        await database.ExecuteAsync("DELETE FROM inventory.employee_snapshot WHERE employee_id = @id", ("id", employee));

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetServices<IJob>().Single(j => j.Name == "identity.employee-publish").RunAsync(Ct);
        }

        await EventuallyAsync(async () => await CopyAsync(employee) is ("Test User", true, { } account) && account == employee);

        await DeliverAsync(new EmployeeDirectoryChanged([new EmployeeSnapshot(employee, "E1", "Newer", false, employee)]) { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        await DeliverAsync(new EmployeeDirectoryChanged([new EmployeeSnapshot(employee, "E1", "Older", true, employee)]) { OccurredAt = DateTimeOffset.UtcNow.AddDays(-1) });
        (await CopyAsync(employee)).ShouldBe(("Newer", false, (Guid?)employee));
    }

    [Fact]
    public async Task The_copy_is_read_only_for_the_application_login()
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var write = new NpgsqlCommand(
            "INSERT INTO inventory.employee_snapshot (employee_id, code, display_name, is_active, source_occurred_at) VALUES (gen_random_uuid(), 'E1', 'X', true, now())",
            connection);

        var error = await Should.ThrowAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe("42501", "only apply_employees writes the copy");
    }

    [Fact]
    public async Task An_employee_whom_the_copy_shows_as_left_gets_no_new_assignment()
    {
        var employee = await database.CreateUserAsync($"copy.left.{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}", [AppRoles.WarehouseKeeper], []);
        await database.ExecuteAsync("UPDATE inventory.employee_snapshot SET is_active = false WHERE employee_id = @id", ("id", employee));
        var warehouse = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'UTN'");

        using var assign = await SendAsync(HttpMethod.Put, $"/api/v1/inventory/warehouses/{warehouse}/responsibles",
            new ResponsiblesRequest([new ResponsibleEntry(employee, "KEEPER")]));

        assign.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await assign.Content.ReadAsStringAsync(Ct));
        (await Problem.ReadAsync(assign)).Code.ShouldBe("Inventory.UnknownEmployee");
    }

    private async Task<Guid> CreateEmployeeAsync()
    {
        var position = (await GetAsync<PagedResponse<PositionSummary>>("/api/v1/identity/positions?search=WAREHOUSE_KEEPER")).Items.Single().Id;
        var code = "E9" + RandomNumberGenerator.GetInt32(0, 100_000).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/identity/employees", new CreateEmployeeRequest(code, _name, null, null, position, new DateOnly(2026, 1, 1)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<EmployeeSummary>(Ct))!.Id;
    }

    private async Task<(string? Name, bool Active, Guid? AccountId)> CopyAsync(Guid employee)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var read = new NpgsqlCommand("SELECT display_name, is_active, account_id FROM inventory.employee_snapshot WHERE employee_id = @id", connection);
        read.Parameters.AddWithValue("id", employee);
        await using var reader = await read.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct)
            ? (reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetGuid(2))
            : (null, false, null);
    }

    private async Task DeliverAsync(EmployeeDirectoryChanged changed)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        foreach (var consumer in scope.ServiceProvider.GetServices<IIntegrationEventConsumer<EmployeeDirectoryChanged>>())
        {
            await IntegrationEventDelivery.DeliverAsync(
                scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, changed, Guid.NewGuid(), Ct);
        }
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Admin.Principal()));
        var response = await browser.Client.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    /// <summary>The single host delivers through the outbox dispatcher's polling interval; waits up to 30 s.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        (await condition()).ShouldBeTrue("the event reached the consumer within 30 s");
    }
}
