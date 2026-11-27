using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The service order API of PR 6h1 (STAGE_6_PLAN 4): list, detail with kit status and allowed actions, create (planned
/// service or repair) with the kit reserved by Inventory afterwards, and start with the state machine's 409s.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ServiceOrderApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("so.engineer", [AppRoles.ServiceEngineer]);
    private static readonly TestUser Manager = TestUser.Create("so.manager", [AppRoles.BrandManager]);
    private static readonly TestUser Planner = TestUser.Create("so.planner", [AppRoles.InventoryPlanner]);
    private static readonly TestUser Sponsor = TestUser.Create("so.sponsor", [AppRoles.Sponsor]);

    private readonly string _tag = "SO" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

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
    public async Task A_planned_service_is_created_at_the_next_threshold_and_Inventory_reserves_its_kit()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("A", oilOnHand: 50, filterOnHand: 5);
        var plan = await PlanOfAsync(machine);

        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan), Manager);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/fleet/service-orders/{created.Order.Id}");
        (created.Order.MachineId, created.Order.DueEngineHours, created.Order.Status, created.Order.PlanName, created.Order.WarehouseCode)
            .ShouldBe((machine, (decimal?)500m, ServiceOrderStatuses.Draft, "Engine service", "KLP"));
        created.Lines.Count.ShouldBe(2);
        created.CreatedBy.ShouldBe(Manager.Id);

        var reserved = await EventuallyAsync(created.Order.Id, d => d.Order.KitStatus == KitStatuses.Reserved);
        reserved.Order.Status.ShouldBe(ServiceOrderStatuses.PartsReserved);
        reserved.KitReservedAt.ShouldNotBeNull();
        reserved.Lines.ShouldAllBe(l => l.QtyMissing == 0 && l.QtyReserved == l.QtyRequired);
    }

    [Fact]
    public async Task A_plan_with_an_open_order_or_of_another_model_is_refused()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("B", oilOnHand: 50, filterOnHand: 5);
        var plan = await PlanOfAsync(machine);
        using (var first = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan), Engineer))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var again = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: plan), Engineer))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(again)).Code.ShouldBe("Fleet.ServiceOrderExists");
        }

        var otherModel = await _seed.ModelAsync();
        var foreignPlan = await database.ScalarAsync<int>("SELECT min(id) FROM fleet.maintenance_plan WHERE model_id = @m", ("m", otherModel));
        using var foreign = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: foreignPlan), Engineer);
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem.ReadAsync(foreign)).Code.ShouldBe("Fleet.MaintenancePlanNotFound");
    }

    [Fact]
    public async Task A_repair_takes_parts_from_the_catalogue_copy_and_refuses_unknown_ones()
    {
        var model = await _seed.ModelAsync();
        var machine = await _seed.MachineAsync(model, "C", (-10, 440));

        using (var unknown = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, Lines: [new(999_999_999, 1)]), Engineer))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(unknown)).Code.ShouldBe("Fleet.UnknownPart");
        }

        using var repair = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, Lines: [new(_seed.KitPartId, 2)]), Engineer);
        repair.StatusCode.ShouldBe(HttpStatusCode.Created, await repair.Content.ReadAsStringAsync(Ct));
        var created = (await repair.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!;
        (created.Order.PlanName, created.Order.DueEngineHours, created.TriggerEngineHours).ShouldBe(((string?)null, (decimal?)null, (decimal?)440m));
        created.Lines.ShouldHaveSingleItem().ShouldBe(new ServiceOrderLineResponse(created.Lines[0].Id, _seed.KitPartId, $"{_tag}-OIL", "Kit oil filter", "PCS", 2, 0, 0, 2));

        // No stock of that part anywhere: Inventory answers with nothing held.
        await EventuallyAsync(created.Order.Id, d => d.Order.KitStatus == KitStatuses.NotHeld);
    }

    [Fact]
    public async Task Start_moves_an_order_to_in_progress_once_and_never_after_cancellation()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("D", oilOnHand: 50, filterOnHand: 5);
        var started = await CreateAsync(machine);
        var cancelled = await CreateRepairAsync(machine);

        using (var response = await PostAsync($"/api/v1/fleet/service-orders/{started}/start", null, Engineer))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Status.ShouldBe(ServiceOrderStatuses.InProgress);
        }

        using (var twice = await PostAsync($"/api/v1/fleet/service-orders/{started}/start", null, Engineer))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await Problem.ReadAsync(twice);
            problem.Code.ShouldBe("SS300");
        }

        await CancelBySqlAsync(cancelled);
        using (var afterCancel = await PostAsync($"/api/v1/fleet/service-orders/{cancelled}/start", null, Engineer))
        {
            afterCancel.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(afterCancel)).Code.ShouldBe("SS300");
        }

        using var missing = await PostAsync("/api/v1/fleet/service-orders/999999999/start", null, Engineer);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem.ReadAsync(missing)).Code.ShouldBe("Fleet.ServiceOrderNotFound");
    }

    [Fact]
    public async Task The_list_filters_and_the_detail_offers_only_what_the_user_may_do()
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync("E", oilOnHand: 50, filterOnHand: 5);
        var order = await CreateAsync(machine);

        var list = await GetAsync<PagedResponse<ServiceOrderSummary>>($"/api/v1/fleet/service-orders?machineId={machine}", Sponsor);
        list.Items.ShouldHaveSingleItem().Id.ShouldBe(order);
        (await GetAsync<PagedResponse<ServiceOrderSummary>>($"/api/v1/fleet/service-orders?machineId={machine}&status=completed", Sponsor)).TotalCount.ShouldBe(0);

        (await GetAsync<ServiceOrderDetails>($"/api/v1/fleet/service-orders/{order}", Engineer)).AllowedActions.ShouldBe([ServiceOrderActions.Start, ServiceOrderActions.Complete, ServiceOrderActions.Cancel]);
        (await GetAsync<ServiceOrderDetails>($"/api/v1/fleet/service-orders/{order}", Sponsor)).AllowedActions.ShouldBeEmpty();

        using var bad = await GetRawAsync("/api/v1/fleet/service-orders?status=parts_reserved", Sponsor);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missing = await GetRawAsync("/api/v1/fleet/service-orders/999999999", Sponsor);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(AppRoles.ServiceEngineer, HttpStatusCode.Created)]
    [InlineData(AppRoles.BrandManager, HttpStatusCode.Created)]
    [InlineData(AppRoles.Administrator, HttpStatusCode.Created)]
    [InlineData(AppRoles.InventoryPlanner, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.WarehouseKeeper, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.Sponsor, HttpStatusCode.Forbidden)]
    public async Task Only_the_roles_of_ManageServiceOrders_create_orders(string role, HttpStatusCode expected)
    {
        var model = await _seed.ModelAsync();
        var machine = await _seed.MachineAsync(model, "F", (-10, 440));

        using var response = await PostAsync("/api/v1/fleet/service-orders",
            new CreateServiceOrderRequest(machine, Lines: [new(_seed.KitPartId, 1)]), TestUser.Create($"so.{role.ToLowerInvariant()}", [role]));

        response.StatusCode.ShouldBe(expected);
    }

    private async Task<long> CreateAsync(long machine)
    {
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, MaintenancePlanId: await PlanOfAsync(machine)), Engineer);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    private async Task<long> CreateRepairAsync(long machine)
    {
        var part = await _seed.StockedPartAsync("R" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)), 5);
        await database.ExecuteAsync(
            "INSERT INTO fleet.part_snapshot (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at) VALUES (@p, 'JD', @o, 'Repair part', 'PCS', true, now())",
            ("p", part), ("o", $"{_tag}-REP-{part}"));
        using var response = await PostAsync("/api/v1/fleet/service-orders", new CreateServiceOrderRequest(machine, Lines: [new(part, 1)]), Engineer);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ServiceOrderDetails>(Ct))!.Order.Id;
    }

    /// <summary>The transitional cancellation (the event flow replaces it in 6h3).</summary>
    private async Task CancelBySqlAsync(long order)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @u, true), set_config('app.user_roles', 'SERVICE_ENGINEER', true)", connection, transaction))
        {
            context.Parameters.AddWithValue("u", Engineer.Id.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using (var cancel = new NpgsqlCommand("SELECT fleet.cancel_service_order(@o, 'Duplicate')", connection, transaction))
        {
            cancel.Parameters.AddWithValue("o", order);
            await cancel.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    private Task<int> PlanOfAsync(long machine) =>
        database.ScalarAsync<int>("SELECT mp.id FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id WHERE m.id = @m", ("m", machine));

    private async Task<ServiceOrderDetails> EventuallyAsync(long order, Func<ServiceOrderDetails, bool> condition)
    {
        ServiceOrderDetails details = null!;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            details = await GetAsync<ServiceOrderDetails>($"/api/v1/fleet/service-orders/{order}", Engineer);
            if (condition(details))
            {
                return details;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        condition(details).ShouldBeTrue($"order {order} did not reach the expected kit state within 30 s (kitStatus {details.Order.KitStatus})");
        return details;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object? body, TestUser user)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.PostAsync(path, body, bearer: _host.Token(user.Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private async Task<HttpResponseMessage> GetRawAsync(string path, TestUser user)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.GetAsync(path, bearer: _host.Token(user.Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private async Task<T> GetAsync<T>(string path, TestUser user)
    {
        using var response = await GetRawAsync(path, user);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
