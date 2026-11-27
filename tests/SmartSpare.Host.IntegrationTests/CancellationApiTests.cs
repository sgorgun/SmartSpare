using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Cancellation and late events (STAGE_6_PLAN 5.5, PR 6h3): cancelling releases the kit's holds through
/// <c>ServiceOrderCancelled</c>; a reservation that reaches an order already cancelled or completed is released at once
/// (<c>ServiceKitReleaseRequested</c>), so cancelling while the reservation is in flight leaves no hold. The late events are
/// produced by delivering the stored order's <c>ServiceOrderDrafted</c> after the order was closed.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class CancellationApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("cancel.engineer", [AppRoles.ServiceEngineer]);

    private readonly string _tag = "CN" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private FleetSeed _seed = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _seed = new FleetSeed(database, _tag);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Cancelling_releases_the_holds_of_the_kit()
    {
        var (machine, oil, _) = await _seed.MachineWithStockedKitAsync("A", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftedOrderAsync(machine);
        await EventuallyAsync(() => HeldAsync(order), held => held == 2);

        using var response = await PostAsync($"/api/v1/fleet/service-orders/{order}/cancel", new CancelServiceOrderRequest("Customer sold the machine"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var cancelled = (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
        (cancelled.Order.Status, cancelled.Order.KitStatus, cancelled.CancelReason)
            .ShouldBe((ServiceOrderStatuses.Cancelled, KitStatuses.Released, "Customer sold the machine"));
        cancelled.Lines.ShouldAllBe(l => l.QtyReserved == 0);
        await EventuallyAsync(() => HeldAsync(order), held => held == 0);
        (await ReservedAsync(oil)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_reservation_that_reaches_a_cancelled_order_is_released_at_once()
    {
        // Cancelled while the reservation was in flight: the cancellation overtakes ServiceOrderDrafted at Inventory.
        var (machine, oil, _) = await _seed.MachineWithStockedKitAsync("B", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftWithoutEventAsync(machine);
        using (var cancel = await PostAsync($"/api/v1/fleet/service-orders/{order}/cancel", new CancelServiceOrderRequest("Duplicate")))
        {
            cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await DeliverDraftedAsync(order);

        // Inventory held the kit, Fleet answered with a release request, and Inventory released it again.
        await EventuallyAsync(() => HoldCountAsync(order, "released"), released => released == 2);
        (await HeldAsync(order)).ShouldBe(0L, "no hold remains");
        (await ReservedAsync(oil)).ShouldBe(0m);
        (await database.ScalarAsync<long>("SELECT count(*) FROM fleet.outbox_message WHERE type LIKE '%.ServiceKitReleaseRequested,%' AND (payload ->> 'serviceOrderId')::bigint = @o", ("o", order)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_reservation_that_reaches_a_completed_order_is_released_and_nothing_is_issued_twice()
    {
        var (machine, oil, _) = await _seed.MachineWithStockedKitAsync("C", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftWithoutEventAsync(machine);
        using (var complete = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventuallyAsync(() => OnHandAsync(oil), onHand => onHand == 20m);

        await DeliverDraftedAsync(order);

        await EventuallyAsync(() => HoldCountAsync(order, "released"), released => released == 2);
        (await HeldAsync(order)).ShouldBe(0L);
        (await OnHandAsync(oil)).ShouldBe(20m, "issued once, at the completion");
    }

    [Fact]
    public async Task Cancel_after_complete_cancel_twice_and_a_missing_reason_are_refused()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("D", oilOnHand: 50, filterOnHand: 5);
        var completed = await DraftedOrderAsync(machine);
        using (var complete = await PostAsync($"/api/v1/fleet/service-orders/{completed}/complete", new CompleteServiceOrderRequest(440)))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var afterComplete = await PostAsync($"/api/v1/fleet/service-orders/{completed}/cancel", new CancelServiceOrderRequest("Too late")))
        {
            afterComplete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(afterComplete)).Code.ShouldBe("SS300");
        }

        var repair = await RepairAsync(machine);
        using (var empty = await PostAsync($"/api/v1/fleet/service-orders/{repair}/cancel", new CancelServiceOrderRequest(" ")))
        {
            empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var first = await PostAsync($"/api/v1/fleet/service-orders/{repair}/cancel", new CancelServiceOrderRequest("Duplicate")))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var twice = await PostAsync($"/api/v1/fleet/service-orders/{repair}/cancel", new CancelServiceOrderRequest("Again"));
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(twice)).Code.ShouldBe("SS300");

        using var planner = await PostAsync($"/api/v1/fleet/service-orders/{repair}/cancel", new CancelServiceOrderRequest("No"),
            TestUser.Create("cancel.planner", [AppRoles.InventoryPlanner]));
        planner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Delivers the stored order's ServiceOrderDrafted to Inventory, as a late delivery would.</summary>
    private async Task DeliverDraftedAsync(long order)
    {
        var lines = await database.ScalarAsync<string>(
            "SELECT string_agg(id || ':' || part_id || ':' || qty_required, ',' ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = @o", ("o", order));
        var number = await database.ScalarAsync<string>("SELECT number FROM fleet.service_order WHERE id = @o", ("o", order));
        var warehouse = await database.ScalarAsync<short>("SELECT warehouse_id FROM fleet.service_order WHERE id = @o", ("o", order));
        var drafted = new ServiceOrderDrafted(order, number, warehouse,
            [.. lines.Split(',').Select(l => l.Split(':')).Select(p => new ServiceOrderKitLine(long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)))],
            Engineer.Id, DateTimeOffset.UtcNow);

        await using var scope = _host.Services.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetServices<IIntegrationEventConsumer<ServiceOrderDrafted>>().ShouldHaveSingleItem();
        (await IntegrationEventDelivery.DeliverAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, drafted, Guid.NewGuid(), Ct))
            .ShouldBeTrue();
    }

    private async Task<long> DraftedOrderAsync(long machine)
    {
        var plan = await database.ScalarAsync<int>("SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m", ("m", machine));
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    private async Task<long> RepairAsync(long machine)
    {
        var part = await _seed.StockedPartAsync("REP", 5);
        await database.ExecuteAsync(
            "INSERT INTO fleet.part_snapshot (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at) VALUES (@p, 'JD', @o, 'Repair part', 'PCS', true, now())",
            ("p", part), ("o", $"{_tag}-REP"));
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, Lines: [new(part, 1)]));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    /// <summary>A draft whose ServiceOrderDrafted was never written (its reservation is "in flight").</summary>
    private async Task<long> DraftWithoutEventAsync(long machine)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @u, true), set_config('app.user_roles', 'SERVICE_ENGINEER', true), set_config('app.user_permissions', '" + string.Join(',', SmartSpare.ApiContracts.Authorization.DefaultRoles.Bundles["SERVICE_ENGINEER"]) + "', true)", connection, transaction))
        {
            context.Parameters.AddWithValue("u", Engineer.Id.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using var draft = new NpgsqlCommand(
            "SELECT fleet.create_service_order_draft(@m, (SELECT mp.id FROM fleet.machine x JOIN fleet.maintenance_plan mp ON mp.model_id = x.model_id WHERE x.id = @m), 500)",
            connection, transaction);
        draft.Parameters.AddWithValue("m", machine);
        var order = (long)(await draft.ExecuteScalarAsync(Ct))!;
        await transaction.CommitAsync(Ct);
        return order;
    }

    private Task<long> HeldAsync(long order) =>
        database.ScalarAsync<long>(
            "SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id WHERE l.service_order_id = @o AND r.status IN ('soft', 'firm')",
            ("o", order));

    private Task<long> HoldCountAsync(long order, string status) =>
        database.ScalarAsync<long>(
            "SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id WHERE l.service_order_id = @o AND r.status = @s::inventory.reservation_status",
            ("o", order), ("s", status));

    private Task<decimal> ReservedAsync(long part) =>
        database.ScalarAsync<decimal>(
            "SELECT reserved_qty FROM inventory.stock_balance b JOIN inventory.warehouse w ON w.id = b.warehouse_id WHERE w.code = 'KLP' AND b.part_id = @p", ("p", part));

    private Task<decimal> OnHandAsync(long part) =>
        database.ScalarAsync<decimal>(
            "SELECT on_hand_qty FROM inventory.stock_balance b JOIN inventory.warehouse w ON w.id = b.warehouse_id WHERE w.code = 'KLP' AND b.part_id = @p", ("p", part));

    private static async Task EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
    {
        var value = await read();
        for (var attempt = 0; attempt < 60 && !condition(value); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            value = await read();
        }

        condition(value).ShouldBeTrue($"the event flow did not reach the expected state within 30 s (last value {value})");
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object? body, TestUser? user = null)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.PostAsync(path, body, bearer: _host.Token((user ?? Engineer).Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }
}
