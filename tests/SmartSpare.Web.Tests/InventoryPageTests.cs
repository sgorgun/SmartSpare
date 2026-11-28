using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The parts catalogue on the Inventory page (STAGE_7_PLAN 3.1, PR 7i1b) on the shared catalogue components: the list,
/// the editor for planners (brand and OEM number fixed after creation, the shared validator), the responsibles panel,
/// and a read-only page for a keeper.
/// </summary>
public sealed class InventoryPageTests : ClientTestContext
{
    private readonly FakePartsCatalogClient _catalog = new();
    private readonly FakePartReferenceClient _inventory = new();

    public InventoryPageTests()
    {
        Services.AddScoped<CatalogClient>(_ => _catalog);
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddScoped<IdentityClient>(_ => new FakeOrganisationClient());
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_list_shows_parts_with_brand_manufacturer_and_cost_from_the_catalogue_route()
    {
        var page = Render<InventoryPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("RE504836"));
        var grid = page.Find("[data-testid=catalog-grid]").TextContent;
        grid.ShouldContain("Engine oil filter");
        grid.ShouldContain("MANN");
        _catalog.Routes.ShouldAllBe(r => r == "api/v1/inventory/parts");
    }

    [Fact]
    public void A_planner_edits_a_part_with_brand_and_number_fixed_and_the_responsibles_panel_below()
    {
        var page = Render<InventoryPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-edit]").ShouldHaveSingleItem());

        page.Find("[data-testid=catalog-edit]").Click();

        page.WaitForAssertion(() => page.Find("input[data-testid=part-code], [data-testid=part-code] input").HasAttribute("disabled").ShouldBeTrue());
        page.Find("[data-testid=responsibles-panel]");
        page.Find("input[data-testid=part-description], [data-testid=part-description] input").Change("Engine oil filter, long life");
        page.Find("[data-testid=catalog-save]").Click();

        page.WaitForAssertion(() => _catalog.Updates.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            u => u.Id.ShouldBe(1042),
            u => u.Version.ShouldBe("9120"),
            u => u.Body.ShouldBe(new PartWrite("RE504836", "Engine oil filter, long life", 2, 11, 1, 0.45m, 18.40m, false, 1))));
    }

    [Fact]
    public void A_description_too_short_is_refused_on_the_form()
    {
        var page = Render<InventoryPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));
        page.WaitForAssertion(() => page.FindAll("[data-testid=catalog-edit]").ShouldHaveSingleItem());

        page.Find("[data-testid=catalog-edit]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=part-description]"));
        page.Find("input[data-testid=part-description], [data-testid=part-description] input").Change("ab");
        page.Find("[data-testid=catalog-save]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-editor-error]").TextContent.ShouldContain("3 to 300 characters"));
        _catalog.Updates.ShouldBeEmpty();
    }

    [Fact]
    public void A_keeper_reads_the_parts_but_gets_no_editing()
    {
        var page = Render<InventoryPage>(p => p.AddCascadingValue(State(User("keeper", AppRoles.WarehouseKeeper))));

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("RE504836"));
        page.FindAll("[data-testid=catalog-new]").ShouldBeEmpty();
        page.FindAll("[data-testid=catalog-edit]").ShouldBeEmpty();
    }
}

/// <summary>A <see cref="CatalogClient"/> with one part.</summary>
public sealed class FakePartsCatalogClient() : CatalogClient(new HttpClient())
{
    public static readonly PartDto Part = new(
        1042, "JD", "RE504836", "Engine oil filter", "Engine oil filters", "PCS", 18.40m, 2, 11, "OIL_FILTER", 1, 0.45m, false, 1, "MANN", false, "9120");

    public List<string> Routes { get; } = [];

    public List<(long Id, PartWrite Body, string Version)> Updates { get; } = [];

    public override Task<ApiResult<PagedResponse<T>>> ListAsync<T>(
        string route, string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Routes.Add(route);
        object items = new PagedResponse<PartDto>(archived ? [] : [Part], page, pageSize, archived ? 0 : 1);
        return Task.FromResult(ApiResult.Success((PagedResponse<T>)items));
    }

    public override Task<ApiResult<T>> UpdateAsync<TWrite, T>(string route, long id, TWrite body, string version, CancellationToken cancellationToken = default)
    {
        Routes.Add(route);
        var write = (PartWrite)(object)body!;
        Updates.Add((id, write, version));
        return Task.FromResult(ApiResult.Success((T)(object)(Part with { Description = write.Name, Version = "9121" })));
    }
}

/// <summary>An <see cref="InventoryClient"/> with the reference lists and an empty responsibles list.</summary>
public sealed class FakePartReferenceClient() : InventoryClient(new HttpClient())
{
    public override Task<ApiResult<List<ReferenceItem>>> ListPartCategoriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new List<ReferenceItem> { new(11, "OIL_FILTER", "Engine oil filters") }));

    public override Task<ApiResult<List<ReferenceItem>>> ListUnitsOfMeasureAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new List<ReferenceItem> { new(1, "PCS", "Pieces") }));

    public override Task<ApiResult<IReadOnlyList<AssignmentRoleDto>>> ListAssignmentRolesAsync(string kind, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success((IReadOnlyList<AssignmentRoleDto>)[new AssignmentRoleDto("part", "BUYER", "Buyer", false)]));

    public override Task<ApiResult<PartAnaloguesView>> GetPartAnaloguesAsync(long partId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PartAnaloguesView(new PartRef(partId, "JD", "RE504836", "Engine oil filter", false), [], [], [])));

    public override Task<ApiResult<IReadOnlyList<ResponsibleDto>>> GetResponsiblesAsync(string segment, long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success((IReadOnlyList<ResponsibleDto>)[]));
}
