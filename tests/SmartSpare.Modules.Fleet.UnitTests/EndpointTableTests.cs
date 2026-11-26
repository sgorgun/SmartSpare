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
        ], ignoreOrder: true);

    [Theory]
    [MemberData(nameof(VersionedRoutes))]
    public void Every_versioned_endpoint_is_named_documented_and_authorized(string route)
    {
        var endpoint = Endpoints.Single(e => Describe(e) == route);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().ShouldNotBeNull(route);
        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary.ShouldNotBeNullOrWhiteSpace(route);
        endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()!.Description.ShouldNotBeNullOrWhiteSpace(route);
        var expected = route.StartsWith("GET ", StringComparison.Ordinal) ? AppPolicies.ReadFleet : AppPolicies.RecordServiceWork;
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldHaveSingleItem().Policy.ShouldBe(expected, route);
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(p => p.StatusCode)
            .ShouldContain(StatusCodes.Status401Unauthorized, route);
    }

    [Fact]
    public void Every_role_may_read_the_fleet() =>
        AppPolicies.Roles[AppPolicies.ReadFleet].ShouldBe(AppRoles.All, ignoreOrder: true);

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
