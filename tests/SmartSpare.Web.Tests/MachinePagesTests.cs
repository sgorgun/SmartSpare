using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Fleet;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The machine screens (STAGE_6_PLAN section 6, PR 6j): the list with due statuses as icon and text, the machine card with
/// its plans and the engine-hours entry: the decimal keypad, the rules checked before sending, the server's refusals as
/// plain sentences with their reference, the drafted orders, and a retry that sends the identical reading.
/// </summary>
public sealed class MachinePagesTests : ClientTestContext
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeFleetClient _fleet = new();

    private readonly FakeTimeProvider _time = new(Now);

    public MachinePagesTests()
    {
        Services.AddScoped<FleetClient>(_ => _fleet);
        Services.AddScoped<InventoryClient>(_ => new FakeWarehouses());
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_list_shows_each_machine_with_its_next_service_as_icon_and_text()
    {
        var page = Render<MachinesPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.Markup.ShouldContain("JD6R150VLN2019OVD");
        page.FindAll("[aria-label='Status: Overdue']").ShouldHaveSingleItem();
        page.FindAll("[aria-label='Status: Due soon']").ShouldHaveSingleItem();
        _fleet.MachineCalls[0].ShouldBe((null, null, null, 1, FleetSearch.DefaultPageSize));
    }

    [Fact]
    public void A_partial_vin_reloads_the_list_from_the_first_page()
    {
        var page = Render<MachinesPage>();
        page.WaitForAssertion(() => _fleet.MachineCalls.Count.ShouldBe(1));

        page.Find("input[inputmode=search]").Input("demo");
        _time.Advance(TimeSpan.FromSeconds(1));   // MudBlazor debounces on the registered TimeProvider

        page.WaitForAssertion(() => _fleet.MachineCalls[^1].Search.ShouldBe("demo"), TimeSpan.FromSeconds(3));
        _fleet.MachineCalls[^1].Page.ShouldBe(1);
    }

    [Fact]
    public void An_error_shows_the_message_with_its_reference_and_an_empty_state()
    {
        _fleet.NextMachines = ApiResult.Failure<PagedResponse<MachineSummary>>(
            new ProblemInfo(503, ProblemCodes.ServiceUnavailable, "Service unavailable", null, "4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a"));

        var page = Render<MachinesPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=machines-error]").TextContent
            .ShouldBe("Something went wrong. Please try again. Reference: 4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a"));
        page.Markup.ShouldContain("The machines could not be loaded.");
    }

    [Fact]
    public void The_card_shows_the_hours_the_plans_and_the_orders_with_their_kit()
    {
        var page = Render<MachinePage>(p => p.Add(x => x.Id, 7L).AddCascadingValue(State(User("engineer", AppRoles.ServiceEngineer))));

        page.WaitForAssertion(() => page.Find("[data-testid=machine-vin]").TextContent.ShouldBe("FT724KLP2024DEMO"));
        page.Find("[data-testid=machine-hours]").TextContent.ShouldContain("1,492 h");
        var plans = page.Find("[data-testid=machine-plans]").TextContent;
        plans.ShouldContain("Next service at 1,500 h");
        plans.ShouldContain("8 h to go");
        plans.ShouldContain("200 h overdue");
        page.FindAll("[aria-label='Status: Reserving parts…']").ShouldHaveSingleItem();
        page.Find("[data-testid=log-hours]");
    }

    [Fact]
    public void The_log_button_opens_the_entry_in_a_titled_dialog()
    {
        var dialogs = Render<MudDialogProvider>();
        var page = Render<MachinePage>(p => p.Add(x => x.Id, 7L).AddCascadingValue(State(User("engineer", AppRoles.ServiceEngineer))));
        page.WaitForAssertion(() => page.Find("[data-testid=log-hours]"));

        page.Find("[data-testid=log-hours]").Click();

        dialogs.WaitForAssertion(() => dialogs.Find("input[data-testid=reading-hours]"));
        dialogs.Find("#entry-title").TextContent.ShouldBe("Log engine hours for FT724KLP2024DEMO");
    }

    [Fact]
    public void A_plan_with_an_open_order_says_which_service_is_ordered()
    {
        var planned = FakeFleetClient.Due("Engine service", 2250, 745, MaintenanceDueStatuses.Planned) with { OpenOrderThreshold = 1500 };
        _fleet.NextMachine = ApiResult.Success(FakeFleetClient.Machine with { Plans = [planned] });

        var page = Render<MachinePage>(p => p.Add(x => x.Id, 7L).AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));

        page.WaitForAssertion(() => page.Find("[data-testid=machine-plans]").TextContent.ShouldContain("Ordered: the service at 1,500 h"));
        page.Find("[data-testid=machine-plans]").TextContent.ShouldContain("Then at 2,250 h");
        page.FindAll("[aria-label='Status: Order planned']").ShouldHaveSingleItem();
    }

    [Fact]
    public void Only_users_who_record_service_work_see_the_log_button()
    {
        var page = Render<MachinePage>(p => p.Add(x => x.Id, 7L).AddCascadingValue(State(User("keeper", AppRoles.WarehouseKeeper))));

        page.WaitForAssertion(() => page.Find("[data-testid=machine-vin]"));
        page.FindAll("[data-testid=log-hours]").ShouldBeEmpty();
    }

    [Fact]
    public void A_loading_error_shows_the_server_message_with_its_reference()
    {
        _fleet.NextMachine = ApiResult.Failure<MachineDetails>(
            new ProblemInfo(404, "Fleet.MachineNotFound", "Not found", "The machine does not exist.", "0af7651916cd43dd8448eb211c80319c"));

        var page = Render<MachinePage>(p => p.Add(x => x.Id, 99L));

        page.WaitForAssertion(() => page.Find("[data-testid=machine-error]").TextContent
            .ShouldBe("The machine does not exist. Reference: 0af7651916cd43dd8448eb211c80319c"));
    }

    [Fact]
    public void The_hours_field_opens_the_decimal_keypad_and_has_a_label()
    {
        var entry = RenderEntry();

        var input = entry.Find("input[data-testid=reading-hours]");
        input.GetAttribute("inputmode").ShouldBe("decimal");
        var label = entry.Find($"label[for='{input.Id}']");
        label.TextContent.ShouldContain("Engine hours");
        entry.Markup.ShouldContain("Last reading: 1,492 h");
    }

    [Fact]
    public void Hours_below_the_last_reading_are_refused_before_anything_is_sent()
    {
        var entry = RenderEntry();

        entry.Find("input[data-testid=reading-hours]").Input("1480");
        entry.Find("[data-testid=reading-save]").Click();

        entry.WaitForAssertion(() => entry.Markup.ShouldContain("The hours cannot be lower than the last reading (1,492 h). Check the hour meter."));
        _fleet.Readings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SS101", "These engine hours are not possible: the engine cannot run more hours than have passed since the last reading.")]
    [InlineData("SS104", "The time is in the future. Enter the time the hours were read.")]
    [InlineData("SS100", "The engine hours are lower than the last reading.")]
    public void A_refusal_of_the_server_reads_as_a_plain_sentence_without_the_code(string code, string message)
    {
        _fleet.NextReading = ApiResult.Failure<RecordReadingResponse>(
            new ProblemInfo(422, code, "Business rule", "raise_exception at fleet.trg_engine_hours_validate()", "1f2e3d4c5b6a79881f2e3d4c5b6a7988"));
        var entry = RenderEntry();

        entry.Find("input[data-testid=reading-hours]").Input("1505");
        entry.Find("[data-testid=reading-save]").Click();

        entry.WaitForAssertion(() => entry.Find("[data-testid=reading-error]").TextContent.ShouldContain(message));
        var text = entry.Find("[data-testid=reading-error]").TextContent;
        text.ShouldContain("Reference: 1f2e3d4c5b6a79881f2e3d4c5b6a7988");
        text.ShouldNotContain(code);
        text.ShouldNotContain("raise_exception");
    }

    [Fact]
    public void A_saved_reading_lists_the_drafted_orders_and_says_the_kit_is_being_reserved()
    {
        var entry = RenderEntry();

        entry.Find("input[data-testid=reading-hours]").Input("1 505,5");
        entry.Find("[data-testid=reading-save]").Click();

        entry.WaitForAssertion(() => entry.Find("[data-testid=reading-drafted]").TextContent.ShouldContain("SO-2026-000501 for the service at 1,500 h"));
        entry.Markup.ShouldContain("The parts are being reserved");
        _fleet.Readings.ShouldHaveSingleItem().ShouldBe((7L, new RecordReadingRequest(1505.5m, Now, ReadingSources.Manual)));
    }

    [Fact]
    public void Saving_again_after_a_lost_answer_sends_the_identical_reading()
    {
        _fleet.NextReading = ApiResult.Failure<RecordReadingResponse>(new ProblemInfo(503, ProblemCodes.ServiceUnavailable, null, null, null));
        var entry = RenderEntry();
        entry.Find("input[data-testid=reading-hours]").Input("1505");
        entry.Find("[data-testid=reading-save]").Click();
        entry.WaitForAssertion(() => entry.Find("[data-testid=reading-error]"));

        _time.Advance(TimeSpan.FromSeconds(40));
        entry.Find("[data-testid=reading-save]").Click();

        entry.WaitForAssertion(() => _fleet.Readings.Count.ShouldBe(2));
        _fleet.Readings[1].ShouldBe(_fleet.Readings[0], "the same time and hours: the server answers it as a replay");
    }

    private IRenderedComponent<EngineHoursEntry> RenderEntry() =>
        Render<EngineHoursEntry>(p => p.Add(x => x.Machine, FakeFleetClient.Machine));
}

/// <summary>A <see cref="FleetClient"/> whose answers the test decides.</summary>
public sealed class FakeFleetClient() : FleetClient(new HttpClient())
{
    public static MaintenancePlanDue Due(string plan, decimal threshold, decimal remaining, string status) => new(
        1, plan, 750, 12, 750, threshold, remaining, new DateOnly(2026, 12, 5), new DateOnly(2027, 3, 15), new DateOnly(2026, 12, 5),
        MaintenanceDueTriggers.EngineHours, status, null);

    public static MachineSummary Summary(long id, string vin, string status) => new(
        id, vin, "724 Vario", "FENDT", "TRACTOR", "CUST-000017", 3, "KLP", 1492, DateTimeOffset.UtcNow.AddDays(-17), "active",
        Due("Engine service", 1500, 8, status));

    public static MachineDetails Machine => new(
        Summary(7, "FT724KLP2024DEMO", MaintenanceDueStatuses.DueSoon),
        2024,
        new DateOnly(2024, 3, 15),
        "NORMAL",
        1.0m,
        1.0m,
        [Due("Engine service", 1500, 8, MaintenanceDueStatuses.DueSoon), Due("Transmission & hydraulic service", 1400, -200, MaintenanceDueStatuses.Overdue)],
        [new ReadingRow(31, new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), 1492, ReadingSources.Import, null)]);

    public List<(string? Search, short? Warehouse, string? DueStatus, int Page, int PageSize)> MachineCalls { get; } = [];

    public List<(long MachineId, RecordReadingRequest Request)> Readings { get; } = [];

    public ApiResult<PagedResponse<MachineSummary>>? NextMachines { get; set; }

    public ApiResult<MachineDetails>? NextMachine { get; set; }

    public ApiResult<RecordReadingResponse>? NextReading { get; set; }

    public override Task<ApiResult<PagedResponse<MachineSummary>>> ListMachinesAsync(
        string? search, short? warehouseId, string? dueStatus, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        MachineCalls.Add((search, warehouseId, dueStatus, page, pageSize));
        return Task.FromResult(NextMachines ?? ApiResult.Success(new PagedResponse<MachineSummary>(
            [Summary(1, "JD6R150VLN2019OVD", MaintenanceDueStatuses.Overdue), Summary(7, "FT724KLP2024DEMO", MaintenanceDueStatuses.DueSoon)], page, pageSize, 2)));
    }

    public override Task<ApiResult<MachineDetails>> GetMachineAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(NextMachine ?? ApiResult.Success(Machine));

    public override Task<ApiResult<RecordReadingResponse>> RecordReadingAsync(long machineId, RecordReadingRequest request, CancellationToken cancellationToken = default)
    {
        Readings.Add((machineId, request));
        var next = NextReading;
        NextReading = null;
        return Task.FromResult(next ?? ApiResult.Success(new RecordReadingResponse(
            new ReadingRow(32, request.ReadAt!.Value, request.Hours, ReadingSources.Manual, Guid.NewGuid()),
            request.Hours,
            false,
            [new DraftedServiceOrder(501, "SO-2026-000501", 1500, false)])));
    }

    public static ServiceOrderSummary OrderSummary(string status, string kit) => new(
        501, "SO-2026-000501", 7, "FT724KLP2024DEMO", "724 Vario", "Engine service", 3, "KLP", status, kit, 750,
        new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), null);

    public static ServiceOrderDetails Order(string status, string kit, IReadOnlyList<string> allowed, decimal reserved = 30) => new(
        OrderSummary(status, kit),
        742,
        null,
        null,
        Guid.NewGuid(),
        kit == KitStatuses.Reserving ? null : DateTimeOffset.UtcNow,
        [new ServiceOrderLineResponse(71, 1203, "F900004.010", "Engine oil 10W-40 Vario", "L", 30, reserved, 0, 30 - reserved),
         new ServiceOrderLineResponse(72, 1210, "F900001.010", "Engine oil filter Vario", "PCS", 1, 1, 0, 0)],
        allowed);

    public List<(string? Status, short? Warehouse, long? Machine)> OrderCalls { get; } = [];

    public List<(long Id, string Action, decimal? Hours, string? Reason)> Actions { get; } = [];

    public List<CreateServiceOrderRequest> Created { get; } = [];

    public List<(short? Warehouse, int Horizon, string? Status)> DueCalls { get; } = [];

    /// <summary>The answers of GET service-orders/{id}, one per call; the last one repeats.</summary>
    public Queue<ApiResult<ServiceOrderDetails>> OrderAnswers { get; } = new();

    public ApiResult<ServiceOrderDetails>? NextAction { get; set; }

    public ApiResult<ServiceOrderDetails>? NextCreate { get; set; }

    public int OrderReads { get; private set; }

    private ApiResult<ServiceOrderDetails>? _lastOrder;

    public override Task<ApiResult<PagedResponse<ServiceOrderSummary>>> ListServiceOrdersAsync(
        string? status, short? warehouseId, long? machineId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        OrderCalls.Add((status, warehouseId, machineId));
        return Task.FromResult(ApiResult.Success(new PagedResponse<ServiceOrderSummary>(
            [OrderSummary(ServiceOrderStatuses.Draft, KitStatuses.Reserving)], page, pageSize, 1)));
    }

    public override Task<ApiResult<ServiceOrderDetails>> GetServiceOrderAsync(long id, CancellationToken cancellationToken = default)
    {
        OrderReads++;
        _lastOrder = OrderAnswers.Count > 0 ? OrderAnswers.Dequeue() : _lastOrder
            ?? ApiResult.Success(Order(ServiceOrderStatuses.PartsReserved, KitStatuses.Reserved,
                [ServiceOrderActions.Start, ServiceOrderActions.Complete, ServiceOrderActions.Cancel]));
        return Task.FromResult(_lastOrder);
    }

    public override Task<ApiResult<ServiceOrderDetails>> ServiceOrderActionAsync(
        long id, string action, decimal? engineHours = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        Actions.Add((id, action, engineHours, reason));
        return Task.FromResult(NextAction ?? ApiResult.Success(Order(
            action switch
            {
                ServiceOrderActions.Start => ServiceOrderStatuses.InProgress,
                ServiceOrderActions.Complete => ServiceOrderStatuses.Completed,
                _ => ServiceOrderStatuses.Cancelled,
            },
            action == ServiceOrderActions.Cancel ? KitStatuses.Released : KitStatuses.Reserved,
            [])));
    }

    public List<(long Id, IReadOnlyList<CreateServiceOrderLine> Lines)> KitEdits { get; } = [];

    public ApiResult<ServiceOrderDetails>? NextKitEdit { get; set; }

    public override Task<ApiResult<ServiceOrderDetails>> ChangeServiceOrderLinesAsync(
        long id, IReadOnlyList<CreateServiceOrderLine> lines, CancellationToken cancellationToken = default)
    {
        KitEdits.Add((id, lines));
        return Task.FromResult(NextKitEdit ?? ApiResult.Success(Order(ServiceOrderStatuses.Draft, KitStatuses.Reserving, [ServiceOrderActions.EditLines])));
    }

    public override Task<ApiResult<ServiceOrderDetails>> CreateServiceOrderAsync(CreateServiceOrderRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        return Task.FromResult(NextCreate ?? ApiResult.Success(Order(ServiceOrderStatuses.Draft, KitStatuses.Reserving, [])));
    }

    public override Task<ApiResult<PagedResponse<DueMaintenanceRow>>> GetDueMaintenanceAsync(
        short? warehouseId, int horizonDays, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        DueCalls.Add((warehouseId, horizonDays, status));
        return Task.FromResult(ApiResult.Success(new PagedResponse<DueMaintenanceRow>(
            [new DueMaintenanceRow(1, "JD6R150VLN2019OVD", "6R 150", "JD", "CUST-000001", 1, "VLN", 2580, Due("Engine service", 2500, -80, MaintenanceDueStatuses.Overdue)),
             new DueMaintenanceRow(7, "FT724KLP2024DEMO", "724 Vario", "FENDT", "CUST-000001", 3, "KLP", 760,
                 Due("Engine service", 1500, 740, MaintenanceDueStatuses.Planned) with { OpenOrderThreshold = 750 })],
            page, pageSize, 2)));
    }
}

/// <summary>The hub list of the filters.</summary>
public sealed class FakeWarehouses() : InventoryClient(new HttpClient())
{
    public override Task<ApiResult<IReadOnlyList<WarehouseResponse>>> ListWarehousesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success<IReadOnlyList<WarehouseResponse>>([]));

    /// <summary>The direct-analogue calls of the service order page (PR 7i3), recorded.</summary>
    public List<(IReadOnlyCollection<long> PartIds, short WarehouseId)> AnalogueCalls { get; } = [];

    public override Task<ApiResult<List<DirectAnalogues>>> ListDirectAnaloguesAsync(
        IReadOnlyCollection<long> partIds, short warehouseId, CancellationToken cancellationToken = default)
    {
        AnalogueCalls.Add((partIds, warehouseId));
        return Task.FromResult(ApiResult.Success(partIds.Select(id => new DirectAnalogues(id, warehouseId,
            id == 1203 ? [new DirectAnalogue(new PartRef(2203, "MANN", "OIL-10W40-AM", "Engine oil 10W-40, aftermarket", false), DirectAnalogueKinds.Interchangeable, 1m, 40m)] : [])).ToList()));
    }
}
