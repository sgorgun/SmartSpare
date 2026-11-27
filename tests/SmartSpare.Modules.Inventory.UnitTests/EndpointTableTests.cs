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
            "GET /api/v1/inventory/imports",
            "GET /api/v1/inventory/imports/{id:long}",
            "GET /internal/v1/inventory/warehouses",
            "GET /api/v1/inventory/assignment-roles",
            "GET /api/v1/inventory/brands",
            "GET /api/v1/inventory/brands/{id:int}/responsibles",
            "PUT /api/v1/inventory/brands/{id:int}/responsibles",
            "GET /api/v1/inventory/warehouses/{id:int}/responsibles",
            "PUT /api/v1/inventory/warehouses/{id:int}/responsibles",

            // The adjustment reasons catalogue (STAGE_7_PLAN 3.1), built by the shared master-data mechanism (ADR 0025).
            "GET /api/v1/inventory/adjustment-reasons",
            "GET /api/v1/inventory/adjustment-reasons/lookup",
            "GET /api/v1/inventory/adjustment-reasons/{id:long}",
            "GET /api/v1/inventory/adjustment-reasons/{id:long}/history",
            "POST /api/v1/inventory/adjustment-reasons",
            "PUT /api/v1/inventory/adjustment-reasons/{id:long}",
            "POST /api/v1/inventory/adjustment-reasons/{id:long}/archive",
            "POST /api/v1/inventory/adjustment-reasons/{id:long}/restore",
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
        AppPolicies.Roles.Keys.ShouldContain(policy!, route);
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(p => p.StatusCode)
            .ShouldContain(StatusCodes.Status401Unauthorized, route);
    }

    [Theory]
    [InlineData("PUT /api/v1/inventory/stock/{warehouseId:int}/{partId:long}/levels", AppPolicies.PlanInventory)]
    [InlineData("POST /api/v1/inventory/stock-adjustments", AppPolicies.MoveStock)]
    [InlineData("GET /api/v1/inventory/stock", AppPolicies.ReadInventory)]
    [InlineData("POST /api/v1/inventory/transfers", AppPolicies.PlanInventory)]
    [InlineData("POST /api/v1/inventory/imports", AppPolicies.ImportData)]
    [InlineData("GET /api/v1/inventory/imports", AppPolicies.ImportData)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/approve", AppPolicies.ApproveTransfer)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/cancel", AppPolicies.CancelTransfer)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/dispatch", AppPolicies.MoveStock)]
    [InlineData("POST /api/v1/inventory/transfers/{id:long}/receive", AppPolicies.MoveStock)]
    [InlineData("GET /internal/v1/inventory/warehouses", AppPolicies.ReadInventory)]
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
