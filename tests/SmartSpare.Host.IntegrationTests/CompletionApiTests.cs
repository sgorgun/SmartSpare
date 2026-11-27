using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Completion and kit issue (STAGE_6_PLAN 5.4, F7, PR 6h2): completing writes <c>ServiceOrderCompleted</c>, Inventory issues
/// the reserved quantities and the rest up to the stock and answers with <c>ServiceKitIssued</c>; the state machine's
/// conflicts are 409 SS300 and an identical repeat is a safe retry. Completing while the kit is still being reserved
/// follows the shortfall rule (the decision recorded in the plan).
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class CompletionApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("done.engineer", [AppRoles.ServiceEngineer]);

    private readonly string _tag = "CP" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

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
    public async Task A_completed_order_gets_its_reserved_kit_issued_and_records_the_visit_reading()
    {
        var (machine, oil, filter) = await _seed.MachineWithStockedKitAsync("A", oilOnHand: 50, filterOnHand: 5);
        var order = await ReservedOrderAsync(machine);
        using (var started = await PostAsync($"/api/v1/fleet/service-orders/{order}/start", null))
        {
            started.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var completed = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(515));

        completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(Ct));
        var body = (await completed.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
        (body.Order.Status, body.CompletedEngineHours, body.Order.KitStatus).ShouldBe((ServiceOrderStatuses.Completed, (decimal?)515m, KitStatuses.Issued));
        var issued = await EventuallyAsync(order, d => d.Lines.All(l => l.QtyIssued == l.QtyRequired));
        issued.Lines.ShouldAllBe(l => l.QtyMissing == 0 && l.QtyReserved == 0);
        (await OnHandAsync(oil)).ShouldBe(20m);
        (await OnHandAsync(filter)).ShouldBe(4m);
        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.stock_movement m JOIN fleet.service_order_line l ON l.id = m.service_order_line_id WHERE l.service_order_id = @o AND m.movement_type_code = 'ISSUE_SERVICE' AND m.created_by = @u",
                ("o", order), ("u", Engineer.Id)))
            .ShouldBe(2, "ISSUE_SERVICE as the engineer who completed the order");
        (await database.ScalarAsync<string>(
                "SELECT source::text FROM fleet.engine_hours_reading WHERE machine_id = @m ORDER BY read_at DESC LIMIT 1", ("m", machine)))
            .ShouldBe("service_visit", "515 h above the machine's 440 h were recorded as a service-visit reading");
    }

    [Fact]
    public async Task A_short_kit_issues_what_is_in_stock_and_shows_the_shortfall()
    {
        var (machine, oil, _) = await _seed.MachineWithStockedKitAsync("B", oilOnHand: 12, filterOnHand: 5);
        var order = await DraftedOrderAsync(machine);
        await EventuallyAsync(order, d => d.Order.KitStatus == KitStatuses.Partial);

        using (var completed = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(Ct));
        }

        var issued = await EventuallyAsync(order, d => d.Lines.Any(l => l.QtyIssued > 0));
        var oilLine = issued.Lines.Single(l => l.PartId == oil);
        (oilLine.QtyRequired, oilLine.QtyIssued, oilLine.QtyMissing).ShouldBe((30m, 12m, 18m));
        (await OnHandAsync(oil)).ShouldBe(0m);
    }

    [Fact]
    public async Task Completing_while_the_kit_is_still_being_reserved_follows_the_shortfall_rule()
    {
        var (machine, oil, filter) = await _seed.MachineWithStockedKitAsync("C", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftWithoutEventAsync(machine);
        (await GetAsync(order)).Order.KitStatus.ShouldBe(KitStatuses.Reserving, "no answer from Inventory yet");

        using (var completed = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            completed.StatusCode.ShouldBe(HttpStatusCode.OK, "not a 409: the work is done (STAGE_6_PLAN 6h decision)");
        }

        var issued = await EventuallyAsync(order, d => d.Lines.All(l => l.QtyIssued == l.QtyRequired));
        issued.Order.KitStatus.ShouldBe(KitStatuses.Issued);
        (await OnHandAsync(oil)).ShouldBe(20m, "issued from stock without a hold");
        (await OnHandAsync(filter)).ShouldBe(4m);
    }

    [Fact]
    public async Task An_identical_repeat_is_a_safe_retry_and_other_hours_are_a_conflict()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("D", oilOnHand: 50, filterOnHand: 5);
        var order = await ReservedOrderAsync(machine);
        using (var first = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventuallyAsync(order, d => d.Lines.All(l => l.QtyIssued == l.QtyRequired));

        using (var repeat = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await repeat.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Status.ShouldBe(ServiceOrderStatuses.Completed);
        }

        using (var other = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(441)))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(other)).Code.ShouldBe("SS300");
        }

        (await database.ScalarAsync<long>("SELECT count(*) FROM fleet.outbox_message WHERE type LIKE '%.ServiceOrderCompleted,%' AND (payload ->> 'serviceOrderId')::bigint = @o", ("o", order)))
            .ShouldBe(1, "the retry wrote no second event");
        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.stock_movement m JOIN fleet.service_order_line l ON l.id = m.service_order_line_id WHERE l.service_order_id = @o AND m.movement_type_code = 'ISSUE_SERVICE'",
                ("o", order)))
            .ShouldBe(2, "nothing issued twice");
    }

    [Fact]
    public async Task Completing_a_cancelled_order_or_below_the_machine_hours_is_refused()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("E", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftedOrderAsync(machine);

        using (var low = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(430)))
        {
            low.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(low)).Code.ShouldBe("Fleet.CompletionHoursTooLow");
        }

        await CancelBySqlAsync(order);
        using (var cancelled = await PostAsync($"/api/v1/fleet/service-orders/{order}/complete", new CompleteServiceOrderRequest(440)))
        {
            cancelled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(cancelled)).Code.ShouldBe("SS300");
        }

        using var missing = await PostAsync("/api/v1/fleet/service-orders/999999999/complete", new CompleteServiceOrderRequest(440));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<long> DraftedOrderAsync(long machine)
    {
        var plan = await database.ScalarAsync<int>("SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m", ("m", machine));
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    private async Task<long> ReservedOrderAsync(long machine)
    {
        var order = await DraftedOrderAsync(machine);
        await EventuallyAsync(order, d => d.Order.KitStatus == KitStatuses.Reserved);
        return order;
    }

    /// <summary>A draft whose ServiceOrderDrafted was never written: its kit reservation stays pending.</summary>
    private Task<long> DraftWithoutEventAsync(long machine) =>
        AsEngineerAsync<long>(
            "SELECT fleet.create_service_order_draft(@m, (SELECT mp.id FROM fleet.machine x JOIN fleet.maintenance_plan mp ON mp.model_id = x.model_id WHERE x.id = @m), 500)",
            ("m", machine));

    private Task<object> CancelBySqlAsync(long order) => AsEngineerAsync<object>("SELECT fleet.cancel_service_order(@o, 'Duplicate')", ("o", order));

    private async Task<T> AsEngineerAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @u, true), set_config('app.user_roles', 'SERVICE_ENGINEER', true), set_config('app.user_permissions', '" + string.Join(',', SmartSpare.ApiContracts.Authorization.DefaultRoles.Bundles["SERVICE_ENGINEER"]) + "', true)", connection, transaction))
        {
            context.Parameters.AddWithValue("u", Engineer.Id.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        T result;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var scalar = await command.ExecuteScalarAsync(Ct);
            result = scalar is T typed ? typed : default!;
        }

        await transaction.CommitAsync(Ct);
        return result;
    }

    private Task<decimal> OnHandAsync(long part) =>
        database.ScalarAsync<decimal>(
            "SELECT on_hand_qty FROM inventory.stock_balance b JOIN inventory.warehouse w ON w.id = b.warehouse_id WHERE w.code = 'KLP' AND b.part_id = @p", ("p", part));

    private async Task<ServiceOrderDetails> EventuallyAsync(long order, Func<ServiceOrderDetails, bool> condition)
    {
        var details = await GetAsync(order);
        for (var attempt = 0; attempt < 60 && !condition(details); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            details = await GetAsync(order);
        }

        condition(details).ShouldBeTrue($"order {order} did not reach the expected state within 30 s");
        return details;
    }

    private async Task<ServiceOrderDetails> GetAsync(long order)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/fleet/service-orders/{order}", bearer: _host.Token(Engineer.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object? body)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.PostAsync(path, body, bearer: _host.Token(Engineer.Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }
}
