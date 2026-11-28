using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The Inventory endpoint table, read without a server: every versioned endpoint is named, documented and behind a
/// policy (ADR 0019, STAGE_3_PLAN 3.2), and the internal query is never part of the public description.
/// </summary>
public sealed class EndpointTableTests
{
    private static readonly IReadOnlyList<RouteEndpoint> Endpoints = Map();

    public static TheoryData<string> VersionedRoutes =>
        [.. Endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal)).Select(Describe)];

    [Fact]
    public void The_api_of_stage_5_is_mapped() =>
        Endpoints.Select(Describe).ShouldBe(
        [
            "GET /api/v1/inventory/parts",
            "GET /api/v1/inventory/parts/{id:long}",
            "GET /api/v1/inventory/warehouses",
            "GET /api/v1/inventory/stock",
            "GET /api/v1/inventory/stock/{warehouseId:int}/{partId:long}",
            "PUT /api/v1/inventory/stock/{warehouseId:int}/{partId:long}/levels",
            "GET /api/v1/inventory/movements",
            "POST /api/v1/inventory/stock-adjustments",
            "GET /api/v1/inventory/transfers",
            "GET /api/v1/inventory/transfers/{id:long}",
            "POST /api/v1/inventory/transfers",
            "POST /api/v1/inventory/transfers/{id:long}/approve",
            "POST /api/v1/inventory/transfers/{id:long}/cancel",
            "POST /api/v1/inventory/transfers/{id:long}/dispatch",
            "POST /api/v1/inventory/transfers/{id:long}/receive",
            "POST /api/v1/inventory/imports",
            "GET /api/v1/inventory/imports/template",
            "GET /api/v1/inventory/imports/export",
            "GET /api/v1/inventory/imports",
            "GET /api/v1/inventory/imports/{id:long}",
            "GET /internal/v1/inventory/warehouses",
            "GET /api/v1/inventory/assignment-roles",
            "GET /api/v1/inventory/brands",
            "GET /api/v1/inventory/brands/{id:int}/responsibles",
            "PUT /api/v1/inventory/brands/{id:int}/responsibles",
            "GET /api/v1/inventory/warehouses/{id:int}/responsibles",
            "PUT /api/v1/inventory/warehouses/{id:int}/responsibles",
            "GET /api/v1/inventory/manufacturers/{id:int}/responsibles",
            "PUT /api/v1/inventory/manufacturers/{id:int}/responsibles",

            // The adjustment reasons catalogue (STAGE_7_PLAN 3.1), built by the shared master-data mechanism (ADR 0025).
            "GET /api/v1/inventory/adjustment-reasons",
            "GET /api/v1/inventory/adjustment-reasons/lookup",
            "GET /api/v1/inventory/adjustment-reasons/{id:long}",
            "GET /api/v1/inventory/adjustment-reasons/{id:long}/history",
            "POST /api/v1/inventory/adjustment-reasons",
            "PUT /api/v1/inventory/adjustment-reasons/{id:long}",
            "POST /api/v1/inventory/adjustment-reasons/{id:long}/archive",
            "POST /api/v1/inventory/adjustment-reasons/{id:long}/restore",
            "GET /api/v1/inventory/adjustment-reasons/csv/template",
            "GET /api/v1/inventory/adjustment-reasons/csv",
            "POST /api/v1/inventory/adjustment-reasons/csv",

            // The brand catalogue (STAGE_7_PLAN 3.1, PR 7h2); its list replaces the read-only one of stage 7 PR 7d.
            "GET /api/v1/inventory/brands/lookup",
            "GET /api/v1/inventory/brands/{id:long}",
            "GET /api/v1/inventory/brands/{id:long}/history",
            "POST /api/v1/inventory/brands",
            "PUT /api/v1/inventory/brands/{id:long}",
            "POST /api/v1/inventory/brands/{id:long}/archive",
            "POST /api/v1/inventory/brands/{id:long}/restore",
            "GET /api/v1/inventory/brands/csv/template",
            "GET /api/v1/inventory/brands/csv",
            "POST /api/v1/inventory/brands/csv",

            // The manufacturer catalogue (STAGE_7_PLAN 3.1, PR 7h3).
            "GET /api/v1/inventory/manufacturers",
            "GET /api/v1/inventory/manufacturers/lookup",
            "GET /api/v1/inventory/manufacturers/{id:long}",
            "GET /api/v1/inventory/manufacturers/{id:long}/history",
            "POST /api/v1/inventory/manufacturers",
            "PUT /api/v1/inventory/manufacturers/{id:long}",
            "POST /api/v1/inventory/manufacturers/{id:long}/archive",
            "POST /api/v1/inventory/manufacturers/{id:long}/restore",
            "GET /api/v1/inventory/manufacturers/csv/template",
            "GET /api/v1/inventory/manufacturers/csv",
            "POST /api/v1/inventory/manufacturers/csv",

            // Parts on the shared catalogue mechanism (PR 7i1); GET /parts above is its list, GET /parts/{id} keeps the stock.
            "GET /api/v1/inventory/parts/lookup",
            "GET /api/v1/inventory/parts/{id:long}/history",
            "POST /api/v1/inventory/parts",
            "PUT /api/v1/inventory/parts/{id:long}",
            "POST /api/v1/inventory/parts/{id:long}/archive",
            "POST /api/v1/inventory/parts/{id:long}/restore",

            // PR 7i1b: part responsibles and the reference data of the part editor.
            "GET /api/v1/inventory/parts/{id:long}/responsibles",
            "PUT /api/v1/inventory/parts/{id:long}/responsibles",
            "GET /api/v1/inventory/part-categories",
            "GET /api/v1/inventory/units-of-measure",
        ], ignoreOrder: true);

    [Theory]
    [MemberData(nameof(VersionedRoutes))]
    public void Every_versioned_endpoint_is_named_documented_and_authorized(string route)
    {
        var endpoint = Endpoints.Single(e => Describe(e) == route);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().ShouldNotBeNull(route);
        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary.ShouldNotBeNullOrWhiteSpace(route);
        endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()!.Description.ShouldNotBeNullOrWhiteSpace(route);
        var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldHaveSingleItem().Policy;
        Permissions.Exists(policy).ShouldBeTrue(route);
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(p => p.StatusCode)
            .ShouldContain(StatusCodes.Status401Unauthorized, route);
    }

    [Theory]
    [InlineData("PUT /api/v1/inventory/stock/{warehouseId:int}/{partId:long}/levels", Permissions.Inventory.Stock.EditLevels)]
    [InlineData("POST /api/v1/inventory/stock-adjustments", Permissions.Inventory.Stock.Adjust)]
    [InlineData("GET /api/v1/inventory/stock", Permissions.Inventory.Stock.View)]
    [InlineData("POST /api/v1/inventory/transfers", Permissions.Inventory.Transfer.Create)]
    [InlineData("POST /api/v1/inventory/imports", Permissions.Inventory.Import.Run)]
    [InlineData("GET /api/v1/inventory/imports/template", Permissions.Inventory.Import.Run)]
    [InlineData("GET /api/v1/inventory/imports/export", Permissions.Inventory.Import.Run)]
    [InlineData("GET /api/v1/inventory/adjustment-reasons/csv", Permissions.Inventory.AdjustmentReason.View)]
    [InlineData("POST /api/v1/inventory/adjustment-reasons/csv", Permissions.Inventory.AdjustmentReason.Edit)]
    [InlineData("POST /api/v1/inventory/brands", Permissions.Inventory.Brand.Create)]
    [InlineData("PUT /api/v1/inventory/brands/{id:long}", Permissions.Inventory.Brand.Edit)]
    [InlineData("POST /api/v1/inventory/brands/{id:long}/archive", Permissions.Inventory.Brand.Archive)]
    [InlineData("POST /api/v1/inventory/brands/csv", Permissions.Inventory.Brand.Edit)]
    [InlineData("GET /api/v1/inventory/manufacturers", Permissions.Inventory.Manufacturer.View)]
    [InlineData("POST /api/v1/inventory/parts", Permissions.Inventory.Part.Create)]
    [InlineData("PUT /api/v1/inventory/parts/{id:long}", Permissions.Inventory.Part.Edit)]
    [InlineData("POST /api/v1/inventory/parts/{id:long}/archive", Permissions.Inventory.Part.Archive)]
    [InlineData("POST /api/v1/inventory/manufacturers", Permissions.Inventory.Manufacturer.Create)]
    [InlineData("POST /api/v1/inventory/manufacturers/{id:long}/archive", Permissions.Inventory.Manufacturer.Archive)]
    [InlineData("PUT /api/v1/inventory/manufacturers/{id:int}/responsibles", Permissions.Inventory.Manufacturer.Edit)]
    [InlineData("PUT /api/v1/inventory/parts/{id:long}/responsibles", Permissions.Inventory.Part.Edit)]
    [InlineData("GET /api/v1/inventory/imports", Permissions.Inventory.Import.View)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/approve", Permissions.Inventory.Transfer.Approve)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/cancel", Permissions.Inventory.Transfer.Cancel)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/dispatch", Permissions.Inventory.Transfer.Dispatch)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/receive", Permissions.Inventory.Transfer.Receive)]
    [InlineData("GET /internal/v1/inventory/warehouses", Permissions.Inventory.Warehouse.View)]
    [InlineData("PUT /api/v1/inventory/brands/{id:int}/responsibles", Permissions.Inventory.Brand.ManageAssignments)]
    [InlineData("PUT /api/v1/inventory/warehouses/{id:int}/responsibles", Permissions.Inventory.Warehouse.ManageAssignments)]
    [InlineData("POST /api/v1/inventory/adjustment-reasons/{id:long}/archive", Permissions.Inventory.AdjustmentReason.Archive)]
    public void Writes_use_their_own_policies(string route, string policy) =>
        Endpoints.Single(e => Describe(e) == route).Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy.ShouldBe(policy);

    [Fact]
    public void The_internal_query_is_not_described() =>
        Endpoints.Single(e => e.RoutePattern.RawText!.StartsWith("/internal/", StringComparison.Ordinal))
            .Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription.ShouldBeTrue();

    private static string Describe(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} {endpoint.RoutePattern.RawText}";

    private static List<RouteEndpoint> Map()
    {
        // The module's services are registered (no database is contacted), so handler parameters bind as services.
        var builder = WebApplication.CreateBuilder();
        var installer = new InventoryModuleInstaller();
        installer.AddServices(builder.Services, builder.Configuration);
        var app = builder.Build();
        installer.MapEndpoints(app);
        ((SmartSpare.Infrastructure.Modules.IModuleInstaller)installer).MapInternalEndpoints(app);
        return
        [
            .. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText!.Contains("/v1/", StringComparison.Ordinal)),
        ];
    }
}
