using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The Fleet endpoint table, read without a server: every versioned endpoint is named, documented and behind a policy
/// (ADR 0019, STAGE_6_PLAN 4).
/// </summary>
public sealed class EndpointTableTests
{
    private static readonly IReadOnlyList<RouteEndpoint> Endpoints = Map();

    public static TheoryData<string> VersionedRoutes => [.. Endpoints.Select(Describe)];

    [Fact]
    public void The_api_of_stage_6_is_mapped() =>
        Endpoints.Select(Describe).ShouldBe(
        [
            "GET /api/v1/fleet/machines",
            "GET /api/v1/fleet/machines/{id:long}",
            "GET /api/v1/fleet/machines/{id:long}/readings",
            "GET /api/v1/fleet/maintenance/due",
            "GET /api/v1/fleet/maintenance-plans",
            "POST /api/v1/fleet/machines/{id:long}/readings",
            "GET /api/v1/fleet/service-orders",
            "GET /api/v1/fleet/service-orders/{id:long}",
            "POST /api/v1/fleet/service-orders",
            "POST /api/v1/fleet/service-orders/{id:long}/start",
            "POST /api/v1/fleet/service-orders/{id:long}/complete",
            "PUT /api/v1/fleet/service-orders/{id:long}/lines",
            "POST /api/v1/fleet/service-orders/{id:long}/cancel",
        ], ignoreOrder: true);

    [Theory]
    [MemberData(nameof(VersionedRoutes))]
    public void Every_versioned_endpoint_is_named_documented_and_authorized(string route)
    {
        var endpoint = Endpoints.Single(e => Describe(e) == route);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().ShouldNotBeNull(route);
        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary.ShouldNotBeNullOrWhiteSpace(route);
        endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()!.Description.ShouldNotBeNullOrWhiteSpace(route);
        var expected = route switch
        {
            _ when route.StartsWith("GET /api/v1/fleet/service-orders", StringComparison.Ordinal) => Permissions.Fleet.ServiceOrder.View,
            _ when route.StartsWith("GET ", StringComparison.Ordinal) => Permissions.Fleet.Machine.View,
            "POST /api/v1/fleet/service-orders" => Permissions.Fleet.ServiceOrder.Create,
            "POST /api/v1/fleet/service-orders/{id:long}/cancel" => Permissions.Fleet.ServiceOrder.Cancel,
            "PUT /api/v1/fleet/service-orders/{id:long}/lines" => Permissions.Fleet.ServiceOrder.Edit,
            "POST /api/v1/fleet/service-orders/{id:long}/start" => Permissions.Fleet.ServiceOrder.Start,
            "POST /api/v1/fleet/service-orders/{id:long}/complete" => Permissions.Fleet.ServiceOrder.Complete,
            _ => Permissions.Fleet.EngineHours.Record,
        };
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldHaveSingleItem().Policy.ShouldBe(expected, route);
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(p => p.StatusCode)
            .ShouldContain(StatusCodes.Status401Unauthorized, route);
    }

    [Fact]
    public void Every_default_role_may_read_the_fleet() =>
        AppRoles.All.ShouldAllBe(r => DefaultRoles.Bundles[r].Contains(Permissions.Fleet.Machine.View) && DefaultRoles.Bundles[r].Contains(Permissions.Fleet.ServiceOrder.View));

    private static string Describe(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} {endpoint.RoutePattern.RawText}";

    private static List<RouteEndpoint> Map()
    {
        // The module's services are registered (no database is contacted), so handler parameters bind as services.
        var builder = WebApplication.CreateBuilder();
        var installer = new FleetModuleInstaller();
        installer.AddServices(builder.Services, builder.Configuration);
        var app = builder.Build();
        installer.MapEndpoints(app);
        return
        [
            .. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal)),
        ];
    }
}
