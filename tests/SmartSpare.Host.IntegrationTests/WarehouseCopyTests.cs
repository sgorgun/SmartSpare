using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// ADR 0029 (PR 7j1): Fleet and Identity keep read-only warehouse copies fed by <c>WarehouseCatalogChanged</c>, filled by
/// <c>inventory.warehouse-catalog-publish</c>, the newest event winning; Fleet refuses an archived hub for a new service
/// order in the API and the database; Inventory projects Fleet's hub references from <c>HubReferencesChanged</c>, written
/// with every draft and cancellation and filled by <c>fleet.hub-reference-publish</c>. Single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class WarehouseCopyTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Admin = TestUser.Create("wh.admin", [AppRoles.Administrator]);
    private static readonly TestUser Engineer = TestUser.Create("wh.engineer", [AppRoles.ServiceEngineer]);
    private static readonly TestUser Manager = TestUser.Create("wh.manager", [AppRoles.BrandManager]);

    private readonly string _tag = "WH" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private FleetSeed _seed = null!;
    private short _warehouseId;
    private string _code = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _seed = new FleetSeed(database, _tag);
        _code = "W" + new string([.. RandomNumberGenerator.GetBytes(6).Select(b => (char)('A' + (b % 26)))]);
        _warehouseId = await database.ScalarAsync<short>(
            "INSERT INTO inventory.warehouse (code, name, city) VALUES (@code, @name, 'Test') RETURNING id", ("code", _code), ("name", $"Hub {_code}"));
    }

    /// <summary>Other tests of the collection count the seven demo hubs: the test hub and every trace of it go.</summary>
    public async ValueTask DisposeAsync()
    {
        await database.ExecuteAsync(
            """
            UPDATE fleet.machine SET home_warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'KLP') WHERE home_warehouse_id = @id;
            DELETE FROM inventory.fleet_hub_reference WHERE warehouse_id = @id;
            DELETE FROM fleet.warehouse_snapshot WHERE warehouse_id = @id;
            DELETE FROM iam.warehouse_snapshot WHERE warehouse_id = @id;
            DELETE FROM inventory.warehouse WHERE id = @id;
            """,
            ("id", _warehouseId));
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task The_publish_job_fills_both_copies_with_the_same_ids_and_an_older_event_never_overwrites_a_newer_one()
    {
        await RunJobAsync("inventory.warehouse-catalog-publish");

        await EventuallyAsync(async () => await CopiesAsync() == ($"Hub {_code}", $"Hub {_code}"));

        var stale = new WarehouseCatalogChanged([new WarehouseSnapshot(_warehouseId, _code, "Stale name", false, false)]) { OccurredAt = DateTimeOffset.UtcNow.AddDays(-1) };
        await DeliverAsync(stale);
        (await CopiesAsync()).ShouldBe(($"Hub {_code}", $"Hub {_code}"));

        var renamed = new WarehouseCatalogChanged([new WarehouseSnapshot(_warehouseId, _code, "Renamed", false, true)]) { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        await DeliverAsync(renamed);
        (await CopiesAsync()).ShouldBe(("Renamed", "Renamed"));
        (await database.ScalarAsync<bool>("SELECT is_archived FROM fleet.warehouse_snapshot WHERE warehouse_id = @id", ("id", _warehouseId))).ShouldBeTrue();
    }

    [Theory]
    [InlineData("fleet")]
    [InlineData("iam")]
    public async Task The_copies_are_read_only_for_the_application_login(string schema)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var write = new NpgsqlCommand(
            $"INSERT INTO {schema}.warehouse_snapshot (warehouse_id, code, name, is_central_hub, is_archived, source_occurred_at) VALUES (32000, 'X', 'X', false, false, now())",
            connection);

        var error = await Should.ThrowAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe("42501", "only apply_warehouses writes the copy");
    }

    [Fact]
    public async Task The_user_form_offers_the_active_hubs_of_Identitys_copy()
    {
        await SetCopiesAsync(archived: false);
        (await FormHubsAsync()).ShouldContain(_code);

        await SetCopiesAsync(archived: true);
        var hubs = await FormHubsAsync();
        hubs.ShouldNotContain(_code);
        hubs.ShouldContain("KLP");
    }

    [Fact]
    public async Task Fleet_refuses_a_new_service_order_at_an_archived_hub_in_the_API_and_the_database()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("A", oilOnHand: 50, filterOnHand: 5);
        await SetCopiesAsync(archived: false);
        await database.ExecuteAsync("UPDATE fleet.machine SET home_warehouse_id = @w WHERE id = @m", ("w", _warehouseId), ("m", machine));
        await SetCopiesAsync(archived: true);

        // The API checks Fleet's copy before drafting.
        var plan = await database.ScalarAsync<int>(
            "SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m ORDER BY mp.interval_hours LIMIT 1",
            ("m", machine));
        using (var create = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan), Manager))
        {
            create.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await create.Content.ReadAsStringAsync(Ct));
            (await Problem.ReadAsync(create)).Code.ShouldBe("SS500");
        }

        // A reading that would draft an order there is refused by the database trigger, and nothing is written.
        using (var reading = await PostAsync($"/api/v1/fleet/machines/{machine}/readings", new RecordReadingRequest(512, DateTimeOffset.UtcNow.AddMinutes(-5)), Engineer))
        {
            reading.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await reading.Content.ReadAsStringAsync(Ct));
            (await Problem.ReadAsync(reading)).Code.ShouldBe("SS500");
        }

        (await database.ScalarAsync<long>("SELECT count(*) FROM fleet.service_order WHERE machine_id = @m", ("m", machine))).ShouldBe(0);

        // Moving a machine to an archived home hub is refused by the database too.
        var other = await _seed.MachineAsync(await _seed.ModelAsync(), "B");
        var error = await Should.ThrowAsync<PostgresException>(() =>
            database.ExecuteAsync("UPDATE fleet.machine SET home_warehouse_id = @w WHERE id = @m", ("w", _warehouseId), ("m", other)));
        error.SqlState.ShouldBe("SS500");
    }

    [Fact]
    public async Task Inventory_projects_the_hub_of_a_drafted_and_a_cancelled_service_order()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("C", oilOnHand: 50, filterOnHand: 5);
        var plan = await database.ScalarAsync<int>(
            "SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m ORDER BY mp.interval_hours LIMIT 1",
            ("m", machine));
        long order;
        using (var create = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan), Manager))
        {
            create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync(Ct));
            order = (await create.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
        }

        await EventuallyAsync(async () => await ReferenceAsync(HubReference.ServiceOrder, order) == true);
        (await database.ScalarAsync<string>(
            "SELECT w.code::text FROM inventory.fleet_hub_reference r JOIN inventory.warehouse w ON w.id = r.warehouse_id WHERE r.record_kind = 'service_order' AND r.record_id = @o",
            ("o", order))).ShouldBe("KLP");

        using (var cancel = await PostAsync($"/api/v1/fleet/service-orders/{order}/cancel", new CancelServiceOrderRequest("Customer withdrew"), Manager))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync(Ct));
        }

        await EventuallyAsync(async () => await ReferenceAsync(HubReference.ServiceOrder, order) == false);
    }

    [Fact]
    public async Task The_Fleet_publish_job_fills_the_projection_and_a_late_event_never_reopens_a_record()
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "D");

        await RunJobAsync("fleet.hub-reference-publish");
        await EventuallyAsync(async () => await ReferenceAsync(HubReference.Machine, machine) == true);

        await DeliverAsync(new HubReferencesChanged([new HubReference(HubReference.Machine, machine, _warehouseId, false)]) { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        (await ReferenceAsync(HubReference.Machine, machine)).ShouldBe(false);

        await DeliverAsync(new HubReferencesChanged([new HubReference(HubReference.Machine, machine, _warehouseId, true)]) { OccurredAt = DateTimeOffset.UtcNow.AddDays(-1) });
        (await ReferenceAsync(HubReference.Machine, machine)).ShouldBe(false, "an older event is ignored");
    }

    private async Task SetCopiesAsync(bool archived) =>
        await database.ExecuteAsync(
            """
            INSERT INTO fleet.warehouse_snapshot (warehouse_id, code, name, is_central_hub, is_archived, source_occurred_at)
            VALUES (@id, @code, @name, false, @archived, now())
            ON CONFLICT (warehouse_id) DO UPDATE SET is_archived = EXCLUDED.is_archived;
            INSERT INTO iam.warehouse_snapshot (warehouse_id, code, name, is_central_hub, is_archived, source_occurred_at)
            VALUES (@id, @code, @name, false, @archived, now())
            ON CONFLICT (warehouse_id) DO UPDATE SET is_archived = EXCLUDED.is_archived;
            """,
            ("id", _warehouseId), ("code", _code), ("name", $"Hub {_code}"), ("archived", archived));

    private async Task<List<string>> FormHubsAsync()
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync("/api/v1/identity/users/form-options", bearer: _host.Token(Admin.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await response.Content.ReadFromJsonAsync<UserFormOptions>(Ct))!.Hubs.Select(h => h.Code)];
    }

    private async Task RunJobAsync(string name)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetServices<IJob>().Single(j => j.Name == name).RunAsync(Ct);
    }

    private async Task DeliverAsync<TEvent>(TEvent integrationEvent)
        where TEvent : SmartSpare.SharedKernel.IntegrationEvent
    {
        await using var scope = _host.Services.CreateAsyncScope();
        foreach (var consumer in scope.ServiceProvider.GetServices<IIntegrationEventConsumer<TEvent>>())
        {
            await IntegrationEventDelivery.DeliverAsync(
                scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, integrationEvent, Guid.NewGuid(), Ct);
        }
    }

    private async Task<(string? Fleet, string? Identity)> CopiesAsync() => (
        await database.ScalarAsync<string?>("SELECT name::text FROM fleet.warehouse_snapshot WHERE warehouse_id = @id", ("id", _warehouseId)),
        await database.ScalarAsync<string?>("SELECT name::text FROM iam.warehouse_snapshot WHERE warehouse_id = @id", ("id", _warehouseId)));

    private Task<bool?> ReferenceAsync(string kind, long id) =>
        database.ScalarAsync<bool?>(
            "SELECT is_open FROM inventory.fleet_hub_reference WHERE record_kind = @k AND record_id = @id", ("k", kind), ("id", id));

    private async Task<HttpResponseMessage> PostAsync(string path, object? body, TestUser user)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.PostAsync(path, body, bearer: _host.Token(user.Principal()));
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

        (await condition()).ShouldBeTrue("the event reached the consumers within 30 s");
    }
}
