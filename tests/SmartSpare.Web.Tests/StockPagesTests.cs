using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The stock screens (STAGE_5_PLAN section 6): the overview with filters, statuses with icon and text, the position with
/// its forms by role, errors with their reference, and the concurrency version sent back when levels change.
/// </summary>
public sealed class StockPagesTests : ClientTestContext
{
    private readonly FakeStockClient _inventory = new();

    public StockPagesTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddLogging();
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_overview_shows_each_position_with_its_status_as_icon_and_text()
    {
        var page = Render<StockPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        page.Markup.ShouldContain("RE900001");
        page.Markup.ShouldContain("Below safety stock");
        page.FindAll("[aria-label='Status: Below safety stock']").ShouldHaveSingleItem();
        _inventory.StockCalls[0].ShouldBe((null, null, null, 1, StockSearch.DefaultPageSize));
    }

    [Fact]
    public void While_the_server_answers_the_grid_says_the_stock_is_loading()
    {
        var pending = new TaskCompletionSource<ApiResult<PagedResponse<StockRow>>>();
        _inventory.NextStock = pending.Task;

        var page = Render<StockPage>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Loading stock…"));
        pending.SetResult(ApiResult.Success(new PagedResponse<StockRow>([FakeStockClient.Row(1, StockStatuses.Ok)], 1, 25, 1)));
        page.WaitForAssertion(() => page.Markup.ShouldNotContain("Loading stock…"));
    }

    [Fact]
    public void An_error_shows_the_message_with_its_reference()
    {
        _inventory.NextStock = Task.FromResult(ApiResult.Failure<PagedResponse<StockRow>>(
            new ProblemInfo(503, ProblemCodes.ServiceUnavailable, "Service unavailable", null, "4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a")));

        var page = Render<StockPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=stock-error]").TextContent
            .ShouldBe("Something went wrong. Please try again. Reference: 4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a"));
        page.Markup.ShouldContain("The stock could not be loaded.");
    }

    [Fact]
    public void A_search_reloads_from_the_first_page_with_the_text()
    {
        var page = Render<StockPage>();
        page.WaitForAssertion(() => _inventory.StockCalls.Count.ShouldBe(1));

        page.Find("input[inputmode=search]").Input("re9");

        page.WaitForAssertion(() => _inventory.StockCalls[^1].Search.ShouldBe("re9"), TimeSpan.FromSeconds(3));
        _inventory.StockCalls[^1].Page.ShouldBe(1);
    }

    [Fact]
    public void A_reader_sees_the_position_without_forms()
    {
        var page = Position(AppRoles.Sponsor);

        page.WaitForAssertion(() => page.Markup.ShouldContain("RE900001 · KLP"));
        page.Find("[data-testid=position-figures]").TextContent.ShouldContain("Available");
        page.Find("[data-testid=reservations]").TextContent.ShouldContain("TR-2026-000412");
        page.Find("[data-testid=movements]").TextContent.ShouldContain("-11");
        page.FindAll("[data-testid=levels-form]").ShouldBeEmpty();
        page.FindAll("[data-testid=adjust-form]").ShouldBeEmpty();
    }

    [Fact]
    public void A_planner_changes_the_levels_against_the_version_read()
    {
        var page = Position(AppRoles.InventoryPlanner);
        page.WaitForAssertion(() => page.Find("[data-testid=levels-form]"));

        page.Find("[data-testid=save-levels]").Click();

        page.WaitForAssertion(() => _inventory.LevelChanges.ShouldHaveSingleItem().Version.ShouldBe("7731"));
        page.FindAll("[data-testid=adjust-form]").ShouldBeEmpty("planners do not move stock");
    }

    [Fact]
    public void A_stale_version_shows_the_conflict_and_reloads_the_position()
    {
        _inventory.NextLevels = ApiResult.Failure<StockRow>(new ProblemInfo(409, "Database.ConcurrencyConflict", "Conflict",
            "The record was changed by someone else. Reload it and try again.", "abc123"));
        var page = Position(AppRoles.InventoryPlanner);
        page.WaitForAssertion(() => page.Find("[data-testid=levels-form]"));

        page.Find("[data-testid=save-levels]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=levels-error]").TextContent
            .ShouldBe("The record was changed by someone else. Reload it and try again. Reference: abc123"));
        _inventory.PositionCalls.ShouldBe(2, "the position is read again");
    }

    [Fact]
    public void A_keeper_posts_an_adjustment_and_needs_a_note()
    {
        var page = Position(AppRoles.WarehouseKeeper);
        page.WaitForAssertion(() => page.Find("[data-testid=adjust-form]"));

        page.Find("input[data-testid=adjust-quantity], [data-testid=adjust-quantity] input").Change("-2");
        page.Find("[data-testid=post-adjustment]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=adjust-error]").TextContent.ShouldContain("Note"));
        _inventory.Adjustments.ShouldBeEmpty("the shared validator stops the request");

        page.Find("input[data-testid=adjust-note], [data-testid=adjust-note] input").Change("Damaged in storage");
        page.Find("[data-testid=post-adjustment]").Click();
        page.WaitForAssertion(() => _inventory.Adjustments.ShouldHaveSingleItem().ShouldBe(new StockAdjustmentRequest(3, 1042, -2, "Damaged in storage")));
    }

    [Fact]
    public void An_unknown_position_shows_the_servers_message()
    {
        _inventory.NextPosition = ApiResult.Failure<StockPositionDetails>(
            new ProblemInfo(404, "Inventory.StockPositionNotFound", "Resource not found", "The part is not stocked in this warehouse.", "t1"));

        var page = Position(AppRoles.Sponsor, partId: 9);

        page.WaitForAssertion(() => page.Find("[data-testid=position-error]").TextContent
            .ShouldBe("The part is not stocked in this warehouse. Reference: t1"));
    }

    private IRenderedComponent<StockPositionPage> Position(string role, long partId = 1042) =>
        Render<StockPositionPage>(p => p
            .Add(x => x.WarehouseId, 3)
            .Add(x => x.PartId, partId)
            .AddCascadingValue(State(User("someone", role))));
}

/// <summary>An <see cref="InventoryClient"/> with a fixed stock position and two overview rows.</summary>
public sealed class FakeStockClient() : InventoryClient(new HttpClient())
{
    public List<(short? Warehouse, string? Status, string? Search, int Page, int PageSize)> StockCalls { get; } = [];

    public List<(ChangeStockLevelsRequest Request, string Version)> LevelChanges { get; } = [];

    public List<StockAdjustmentRequest> Adjustments { get; } = [];

    public int PositionCalls { get; private set; }

    public Task<ApiResult<PagedResponse<StockRow>>>? NextStock { get; set; }

    public ApiResult<StockPositionDetails>? NextPosition { get; set; }

    public ApiResult<StockRow>? NextLevels { get; set; }

    public static StockRow Row(int number, string status) => new(
        3, "KLP", 1040 + number, "JD", $"RE90000{number}", "Engine oil filter", "PCS", 24, 13, 11, 6, 30, 4, status, null);

    public static StockPositionDetails Position => new(
        Row(2, StockStatuses.Ok) with { PartId = 1042, OemPartNumber = "RE900001" },
        "A-12-03",
        "7731",
        [new ReservationSummary(5521, 11, "firm", null, 412, "TR-2026-000412", null, DateTimeOffset.UtcNow)],
        [new MovementRow(884210, DateTimeOffset.UtcNow, 3, "KLP", 1042, "RE900001", "TRANSFER_OUT", -11, "TR-2026-000412", null)]);

    public override Task<ApiResult<IReadOnlyList<WarehouseResponse>>> ListWarehousesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success<IReadOnlyList<WarehouseResponse>>([]));

    public override Task<ApiResult<PagedResponse<StockRow>>> GetStockAsync(
        short? warehouseId, string? status, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        StockCalls.Add((warehouseId, status, search, page, pageSize));
        if (NextStock is { } next)
        {
            NextStock = null;
            return next;
        }

        return Task.FromResult(ApiResult.Success(new PagedResponse<StockRow>(
            [Row(1, StockStatuses.BelowSafety) with { OemPartNumber = "RE900001" }, Row(2, StockStatuses.Ok)], page, pageSize, 2)));
    }

    public override Task<ApiResult<StockPositionDetails>> GetStockPositionAsync(short warehouseId, long partId, CancellationToken cancellationToken = default)
    {
        PositionCalls++;
        return Task.FromResult(NextPosition ?? ApiResult.Success(Position));
    }

    public override Task<ApiResult<StockRow>> ChangeStockLevelsAsync(
        short warehouseId, long partId, ChangeStockLevelsRequest request, string version, CancellationToken cancellationToken = default)
    {
        LevelChanges.Add((request, version));
        return Task.FromResult(NextLevels ?? ApiResult.Success(Position.Stock));
    }

    public override Task<ApiResult<StockAdjustmentResponse>> AdjustStockAsync(StockAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        Adjustments.Add(request);
        return Task.FromResult(ApiResult.Success(new StockAdjustmentResponse(1, Position.Stock)));
    }
}
