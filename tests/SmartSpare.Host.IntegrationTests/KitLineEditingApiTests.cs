using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Editing the kit of an open order (PR 6i, #140) through <c>PUT /service-orders/{id}/lines</c> and the event
/// <c>ServiceOrderLinesChanged</c>: Inventory releases the holds of removed lines, a changed quantity is released and
/// reserved anew, new parts are reserved, and the order shows the new kit as reserved once Inventory answered. The same
/// kit again publishes nothing; once the work started the kit is fixed (409 SS300); a repair keeps a line.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class KitLineEditingApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("kit.engineer", [AppRoles.ServiceEngineer]);

    private readonly string _tag = "KE" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

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
    public async Task Removed_changed_and_added_lines_adjust_the_holds()
    {
        var (machine, oil, filter) = await _seed.MachineWithStockedKitAsync("A", oilOnHand: 50, filterOnHand: 5);
        var extra = await NewPartAsync("EXTRA", onHand: 4);
        var order = await DraftedOrderAsync(machine);
        await EventuallyAsync(() => OrderAsync(order), o => o.Order.KitStatus == KitStatuses.Reserved);
        (await ReservedAsync(oil)).ShouldBe(30m);

        // Oil down to 20 L (a new line), the filter removed, two of a new part added.
        using var response = await PutAsync(order, [new(oil, 20), new(extra, 2)]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var edited = (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
        (edited.Order.Status, edited.Order.KitStatus).ShouldBe((ServiceOrderStatuses.Draft, KitStatuses.Reserving));
        edited.Lines.Select(l => (l.PartId, l.QtyRequired)).OrderBy(l => l.PartId).ShouldBe([(oil, 20m), (extra, 2m)], ignoreOrder: true);

        var reserved = await EventuallyAsync(() => OrderAsync(order), o => o.Order.KitStatus == KitStatuses.Reserved);
        reserved.Order.Status.ShouldBe(ServiceOrderStatuses.PartsReserved);
        reserved.Lines.ShouldAllBe(l => l.QtyReserved == l.QtyRequired);
        (await ReservedAsync(oil)).ShouldBe(20m, "the 30 L hold was released and 20 L reserved for the new line");
        (await ReservedAsync(filter)).ShouldBe(0m, "the removed filter's hold was released");
        (await ReservedAsync(extra)).ShouldBe(2m);
        (await ActiveHoldsAsync(order)).ShouldBe(2L, "one hold per remaining line, none for the removed ones");
    }

    [Fact]
    public async Task The_same_kit_again_publishes_nothing_and_a_started_order_keeps_its_kit()
    {
        var (machine, oil, filter) = await _seed.MachineWithStockedKitAsync("B", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftedOrderAsync(machine);
        await EventuallyAsync(() => OrderAsync(order), o => o.Order.KitStatus == KitStatuses.Reserved);

        using (var same = await PutAsync(order, [new(filter, 1), new(oil, 30)]))
        {
            same.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM fleet.outbox_message WHERE type LIKE '%.ServiceOrderLinesChanged,%' AND (payload ->> 'serviceOrderId')::bigint = @o", ("o", order)))
            .ShouldBe(0);

        using (var start = await PostAsync($"/api/v1/fleet/service-orders/{order}/start"))
        {
            start.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var started = await PutAsync(order, [new(oil, 10)]);
        started.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(started)).Code.ShouldBe("SS300");
    }

    [Fact]
    public async Task A_repair_keeps_a_line_and_unknown_parts_are_refused()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("C", oilOnHand: 50, filterOnHand: 5);
        var part = await NewPartAsync("REP", onHand: 3);
        using var created = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, Lines: [new(part, 1)]));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var repair = (await created.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;

        using (var empty = await PutAsync(repair, []))
        {
            empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(empty)).Code.ShouldBe("Fleet.NoLines");
        }

        using var unknown = await PutAsync(repair, [new(part, 1), new(long.MaxValue - 7, 1)]);
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(unknown)).Code.ShouldBe("Fleet.UnknownPart");

        using var planner = await PutAsync(repair, [new(part, 2)], TestUser.Create("kit.planner", [AppRoles.InventoryPlanner]));
        planner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<long> NewPartAsync(string suffix, decimal onHand)
    {
        var part = await _seed.StockedPartAsync(suffix, onHand);
        await database.ExecuteAsync(
            "INSERT INTO fleet.part_snapshot (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at) VALUES (@p, 'JD', @o, 'Kit edit part', 'PCS', true, now())",
            ("p", part), ("o", $"{_tag}-{suffix}"));
        return part;
    }

    private async Task<long> DraftedOrderAsync(long machine)
    {
        var plan = await database.ScalarAsync<int>("SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m", ("m", machine));
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    private async Task<ServiceOrderDetails> OrderAsync(long order)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/fleet/service-orders/{order}", bearer: _host.Token(Engineer.Principal()));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
    }

    private Task<decimal> ReservedAsync(long part) =>
        database.ScalarAsync<decimal>(
            "SELECT reserved_qty FROM inventory.stock_balance b JOIN inventory.warehouse w ON w.id = b.warehouse_id WHERE w.code = 'KLP' AND b.part_id = @p", ("p", part));

    private Task<long> ActiveHoldsAsync(long order) =>
        database.ScalarAsync<long>(
            "SELECT count(*) FROM inventory.stock_reservation r WHERE r.status IN ('soft', 'firm') AND r.service_order_line_id IN (SELECT id FROM fleet.service_order_line WHERE service_order_id = @o)",
            ("o", order));

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> condition)
    {
        var value = await read();
        for (var attempt = 0; attempt < 60 && !condition(value); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            value = await read();
        }

        condition(value).ShouldBeTrue("the event flow did not reach the expected state within 30 s");
        return value;
    }

    private Task<HttpResponseMessage> PutAsync(long order, IReadOnlyList<CreateServiceOrderLine> lines, TestUser? user = null) =>
        SendAsync(HttpMethod.Put, $"/api/v1/fleet/service-orders/{order}/lines", new ChangeServiceOrderLinesRequest(lines), user);

    private Task<HttpResponseMessage> PostAsync(string path, object? body = null, TestUser? user = null) => SendAsync(HttpMethod.Post, path, body, user);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, TestUser? user)
    {
        using var browser = _host.NewBrowser();
        var response = method == HttpMethod.Put
            ? await browser.PutAsync(path, body, bearer: _host.Token((user ?? Engineer).Principal()))
            : await browser.PostAsync(path, body, bearer: _host.Token((user ?? Engineer).Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }
}
