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

/// <summary>The manufacturers page (STAGE_7_PLAN 3.1, PR 7h3) on the shared catalogue components.</summary>
public sealed class ManufacturersPageTests : ClientTestContext
{
    private readonly FakeManufacturerClient _catalog = new();

    public ManufacturersPageTests()
    {
        Services.AddScoped<CatalogClient>(_ => _catalog);
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void A_planner_sees_the_website_and_creates_a_manufacturer_with_an_optional_country()
    {
        var page = Render<ManufacturersPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("https://www.mann-hummel.com"));

        page.Find("[data-testid=catalog-new]").Click();
        page.Find("input[data-testid=manufacturer-code], [data-testid=manufacturer-code] input").Change("bosch");
        page.Find("input[data-testid=manufacturer-name], [data-testid=manufacturer-name] input").Change("Bosch");
        page.Find("input[data-testid=manufacturer-website], [data-testid=manufacturer-website] input").Change("not a url");
        page.Find("[data-testid=catalog-save]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=catalog-editor-error]").TextContent.ShouldContain("http(s) address"));

        page.Find("input[data-testid=manufacturer-website], [data-testid=manufacturer-website] input").Change("https://www.bosch.com");
        page.Find("[data-testid=catalog-save]").Click();
        page.WaitForAssertion(() => _catalog.Creates.ShouldHaveSingleItem().ShouldBe(new ManufacturerWrite("bosch", "Bosch", null, "https://www.bosch.com")));
    }

    [Fact]
    public void A_keeper_reads_manufacturers_but_gets_no_editing()
    {
        var page = Render<ManufacturersPage>(p => p.AddCascadingValue(State(User("keeper", AppRoles.WarehouseKeeper))));

        page.WaitForAssertion(() => page.Find("[data-testid=catalog-grid]").TextContent.ShouldContain("MANN+HUMMEL"));
        page.FindAll("[data-testid=catalog-new]").ShouldBeEmpty();
    }
}

/// <summary>A <see cref="CatalogClient"/> with one manufacturer.</summary>
public sealed class FakeManufacturerClient() : CatalogClient(new HttpClient())
{
    public List<ManufacturerWrite> Creates { get; } = [];

    public override Task<ApiResult<PagedResponse<T>>> ListAsync<T>(
        string route, string? search, bool archived, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        object items = new PagedResponse<ManufacturerDto>([new(1, "MANN", "MANN+HUMMEL", "DE", "https://www.mann-hummel.com", false, "1")], page, pageSize, 1);
        return Task.FromResult(ApiResult.Success((PagedResponse<T>)items));
    }

    public override Task<ApiResult<T>> CreateAsync<TWrite, T>(string route, TWrite body, CancellationToken cancellationToken = default)
    {
        var write = (ManufacturerWrite)(object)body!;
        Creates.Add(write);
        return Task.FromResult(ApiResult.Success((T)(object)new ManufacturerDto(9, write.Code.ToUpperInvariant(), write.Name, write.CountryCode, write.Website, false, "1")));
    }
}
