using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The brands page (STAGE_7_PLAN 3.1, PR 7h2) on the shared catalogue components.</summary>
public sealed class BrandsPageTests : ClientTestContext
{
    private readonly FakeBrandClient _catalog = new();

    public BrandsPageTests()
    {
        Services.AddScoped<CatalogClient>(_ => _catalog);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_list_shows_code_name_and_country_and_a_planner_creates_a_brand_with_the_shared_validator()
    {
        var page = Render<BrandsPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("John Deere"));
        _catalog.Routes.ShouldAllBe(r => r == "api/v1/inventory/brands");

        page.Find("[data-testid=catalog-new]").Click();
        page.Find("input[data-testid=brand-code], [data-testid=brand-code] input").Change("fendt");
        page.Find("input[data-testid=brand-name], [data-testid=brand-name] input").Change("Fendt");
        page.Find("input[data-testid=brand-country], [data-testid=brand-country] input").Change("DEU");
        page.Find("[data-testid=catalog-save]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-editor-error]").TextContent.ShouldContain("two-letter country code"));
        _catalog.Creates.ShouldBeEmpty();

        page.Find("input[data-testid=brand-country], [data-testid=brand-country] input").Change("de");
        page.Find("[data-testid=catalog-save]").Click();
        page.WaitForAssertion(() => _catalog.Creates.ShouldHaveSingleItem().ShouldBe(new BrandWrite("fendt", "Fendt", "de")));
    }

    [Fact]
    public void A_keeper_reads_brands_but_gets_no_editing()
    {
        var page = Render<BrandsPage>(p => p.AddCascadingValue(State(User("keeper", AppRoles.WarehouseKeeper))));

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("CLAAS"));
        page.FindAll("[data-testid=catalog-new]").ShouldBeEmpty();
    }
}

/// <summary>A <see cref="CatalogClient"/> with two brands.</summary>
public sealed class FakeBrandClient() : CatalogClient(new HttpClient())
{
    public List<string> Routes { get; } = [];

    public List<BrandWrite> Creates { get; } = [];

    public override Task<ApiResult<PagedResponse<T>>> ListAsync<T>(
        string route, string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Routes.Add(route);
        object items = new PagedResponse<BrandDto>([new(1, "CLAAS", "CLAAS", "DE", false, "1"), new(2, "JD", "John Deere", "US", false, "1")], page, pageSize, 2);
        return Task.FromResult(ApiResult.Success((PagedResponse<T>)items));
    }

    public override Task<ApiResult<T>> CreateAsync<TWrite, T>(string route, TWrite body, CancellationToken cancellationToken = default)
    {
        var write = (BrandWrite)(object)body!;
        Creates.Add(write);
        return Task.FromResult(ApiResult.Success((T)(object)new BrandDto(9, write.Code.ToUpperInvariant(), write.Name, write.CountryCode.ToUpperInvariant(), false, "1")));
    }
}
