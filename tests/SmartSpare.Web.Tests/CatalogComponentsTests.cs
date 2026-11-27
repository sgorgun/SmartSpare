using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The shared catalogue components (ADR 0025, STAGE_7_PLAN 2.7) on the adjustment reasons screen: list, editor with the
/// shared validator and the 409 reload, archive with its SS501 reasons, history in plain words, and the autocomplete.
/// </summary>
public sealed class CatalogComponentsTests : ClientTestContext
{
    private readonly FakeCatalogClient _catalog = new();

    public CatalogComponentsTests()
    {
        Services.AddScoped<CatalogClient>(_ => _catalog);
        Services.AddLogging();
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_list_shows_each_reason_with_its_direction_in_words()
    {
        var page = Reasons(AppRoles.InventoryPlanner);

        page.WaitForAssertion(() => page.FindAll("[data-testid=reason-direction]").Select(e => e.TextContent)
            .ShouldBe(["Surplus or shortage", "Shortage only"]));
        page.Markup.ShouldContain("COUNT_CORRECTION");
        _catalog.Lists.ShouldHaveSingleItem().ShouldBe(("api/v1/inventory/adjustment-reasons", (string?)null, false));
    }

    [Fact]
    public void A_keeper_reads_the_reasons_but_cannot_change_them()
    {
        var page = Reasons(AppRoles.WarehouseKeeper);

        page.WaitForAssertion(() => page.Markup.ShouldContain("DAMAGED"));
        page.FindAll("[data-testid=catalog-new]").ShouldBeEmpty();
        page.FindAll("[data-testid=catalog-edit]").ShouldBeEmpty();
        page.FindAll("[data-testid=catalog-archive]").ShouldBeEmpty();
        page.FindAll("[data-testid=catalog-history]").Count.ShouldBe(2, "everyone who reads may see the history");
    }

    [Fact]
    public void A_new_reason_is_checked_by_the_shared_validator_before_it_is_sent()
    {
        var page = Reasons(AppRoles.InventoryPlanner);
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-new]"));

        page.Find("[data-testid=catalog-new]").Click();
        page.Find("[data-testid=catalog-save]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-editor-error]").TextContent.ShouldContain("2 to 30"));
        _catalog.Creates.ShouldBeEmpty();

        page.Find("input[data-testid=reason-code], [data-testid=reason-code] input").Change("wet");
        page.Find("input[data-testid=reason-name], [data-testid=reason-name] input").Change("Water damage");
        page.Find("[data-testid=catalog-save]").Click();

        page.WaitForAssertion(() => _catalog.Creates.ShouldHaveSingleItem().ShouldBe(new AdjustmentReasonWrite("wet", "Water damage", AdjustmentDirections.Out, false)));
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-editor]").ShouldBeEmpty());
    }

    [Fact]
    public void A_stale_edit_shows_the_current_version_and_says_so()
    {
        _catalog.NextUpdate = ApiResult.Failure<AdjustmentReasonDto>(new ProblemInfo(409, "Database.ConcurrencyConflict", null, "Changed by someone else.", "t1"));
        _catalog.Current = FakeCatalogClient.Damaged with { Name = "Damaged in the yard", Version = "9" };
        var page = Reasons(AppRoles.InventoryPlanner);
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-edit]").Count.ShouldBe(2));

        page.FindAll("[data-testid=catalog-edit]")[1].Click();
        page.Find("input[data-testid=reason-name], [data-testid=reason-name] input").Change("Broken");
        page.Find("[data-testid=catalog-save]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-editor-notice]").TextContent.ShouldContain("Someone changed this record"));
        _catalog.Updates.ShouldHaveSingleItem().Version.ShouldBe("2", "the update sent the version the user read");
        page.Find("input[data-testid=reason-name], [data-testid=reason-name] input").GetAttribute("value").ShouldBe("Damaged in the yard");
    }

    [Fact]
    public void An_archive_asks_first_and_lists_what_blocks_it()
    {
        _catalog.NextAction = ApiResult.Failure<JsonElement>(
            new ProblemInfo(409, "SS501", null, "In use.", "t2", ["on hand 12 at KLP", "3 open transfers"]));
        var page = Reasons(AppRoles.InventoryPlanner);
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-archive]").Count.ShouldBe(2));

        page.FindAll("[data-testid=catalog-archive]")[0].Click();
        page.Find("[data-testid=catalog-archive-confirm]").TextContent.ShouldContain("Archive Stock count correction?");
        _catalog.Actions.ShouldBeEmpty("nothing is archived before the confirmation");
        page.Find("[data-testid=catalog-archive-yes]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-archive-blocked]").QuerySelectorAll("li").Select(li => li.TextContent.Trim())
            .ShouldBe(["on hand 12 at KLP", "3 open transfers"]));
        _catalog.Actions.ShouldHaveSingleItem().ShouldBe((1L, "archive", "1"));
    }

    [Fact]
    public void The_history_says_who_changed_what_and_when()
    {
        var page = Reasons(AppRoles.WarehouseKeeper);
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-history]").Count.ShouldBe(2));

        page.FindAll("[data-testid=catalog-history]")[1].Click();

        page.WaitForAssertion(() => page.FindAll("[data-testid=history-entry]").Count.ShouldBe(2));
        var entries = page.FindAll("[data-testid=history-entry]");
        entries[0].TextContent.ShouldContain("Rasa K. changed it");
        entries[0].QuerySelector("[data-testid=history-change]")!.TextContent.ShouldBe("Name: Damaged → Damaged in storage");
        entries[1].TextContent.ShouldContain("The system created it");
        _catalog.Histories.ShouldHaveSingleItem().ShouldBe(2L);
    }

    [Fact]
    public void The_autocomplete_offers_active_entries_as_the_user_types()
    {
        var picker = Render<CatalogAutocomplete>(p => p
            .Add(c => c.Route, "api/v1/inventory/adjustment-reasons")
            .Add(c => c.Label, "Reason"));

        picker.Find("input").Input("dam");

        picker.WaitForAssertion(() => _catalog.Lookups.ShouldContain("dam"), TimeSpan.FromSeconds(3));
    }

    private IRenderedComponent<AdjustmentReasonsPage> Reasons(string role) =>
        Render<AdjustmentReasonsPage>(p => p.AddCascadingValue(State(User("planner", role))));
}

/// <summary>A <see cref="CatalogClient"/> whose answers the test decides.</summary>
public sealed class FakeCatalogClient() : CatalogClient(new HttpClient())
{
    public static readonly AdjustmentReasonDto CountCorrection = new(1, "COUNT_CORRECTION", "Stock count correction", AdjustmentDirections.Both, true, false, "1");

    public static readonly AdjustmentReasonDto Damaged = new(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out, false, false, "2");

    public List<(string Route, string? Search, bool Archived)> Lists { get; } = [];

    public List<AdjustmentReasonWrite> Creates { get; } = [];

    public List<(long Id, AdjustmentReasonWrite Body, string Version)> Updates { get; } = [];

    public List<(long Id, string Action, string Version)> Actions { get; } = [];

    public List<long> Histories { get; } = [];

    public List<string?> Lookups { get; } = [];

    public ApiResult<AdjustmentReasonDto>? NextUpdate { get; set; }

    public ApiResult<JsonElement>? NextAction { get; set; }

    public AdjustmentReasonDto Current { get; set; } = Damaged;

    public override Task<ApiResult<PagedResponse<T>>> ListAsync<T>(
        string route, string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Lists.Add((route, search, archived));
        object items = new PagedResponse<AdjustmentReasonDto>([CountCorrection, Damaged], page, pageSize, 2);
        return Task.FromResult(ApiResult.Success((PagedResponse<T>)items));
    }

    public override Task<ApiResult<T>> GetAsync<T>(string route, long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success((T)(object)Current));

    public override Task<ApiResult<T>> CreateAsync<TWrite, T>(string route, TWrite body, CancellationToken cancellationToken = default)
    {
        var write = (AdjustmentReasonWrite)(object)body!;
        Creates.Add(write);
        return Task.FromResult(ApiResult.Success((T)(object)new AdjustmentReasonDto(9, write.Code.ToUpperInvariant(), write.Name, write.Direction, write.RequiresNote, false, "1")));
    }

    public override Task<ApiResult<T>> UpdateAsync<TWrite, T>(string route, long id, TWrite body, string version, CancellationToken cancellationToken = default)
    {
        Updates.Add((id, (AdjustmentReasonWrite)(object)body!, version));
        var result = NextUpdate ?? ApiResult.Success(Damaged);
        return Task.FromResult(result.IsSuccess ? ApiResult.Success((T)(object)result.Value!) : ApiResult.Failure<T>(result.Problem!));
    }

    public override Task<ApiResult<JsonElement>> ActionAsync(string route, long id, string action, string version, CancellationToken cancellationToken = default)
    {
        Actions.Add((id, action, version));
        return Task.FromResult(NextAction ?? ApiResult.Success(default(JsonElement)));
    }

    public override Task<ApiResult<PagedResponse<RecordHistoryEntry>>> HistoryAsync(string route, long id, int page, CancellationToken cancellationToken = default)
    {
        Histories.Add(id);
        return Task.FromResult(ApiResult.Success(new PagedResponse<RecordHistoryEntry>(
        [
            new(new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.Zero), "changed", "Rasa K.", [new("Name", "Damaged", "Damaged in storage")], null),
            new(new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero), "created", null, [new("Code", null, "DAMAGED")], null),
        ], 1, 20, 2)));
    }

    public override Task<ApiResult<List<CatalogLookupItem>>> LookupAsync(string route, string? search, CancellationToken cancellationToken = default)
    {
        Lookups.Add(search);
        return Task.FromResult(ApiResult.Success(new List<CatalogLookupItem> { new(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out) }));
    }
}
