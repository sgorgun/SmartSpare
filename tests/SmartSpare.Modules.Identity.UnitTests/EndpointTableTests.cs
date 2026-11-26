using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// The Identity endpoint table, read without a server: every versioned endpoint is named, documented and behind the
/// right policy (ADR 0019, STAGE_3_PLAN 3.2); user administration needs Administration, the own password only a user.
/// </summary>
public sealed class EndpointTableTests
{
    private static readonly IReadOnlyList<RouteEndpoint> Endpoints = Map();

    public static TheoryData<string> Routes => [.. Endpoints.Select(Describe)];

    [Fact]
    public void The_user_administration_api_is_mapped() =>
        Endpoints.Select(Describe).ShouldBe(
        [
            "GET /api/v1/identity/users/",
            "GET /api/v1/identity/users/form-options",
            "POST /api/v1/identity/users/",
            "PUT /api/v1/identity/users/{id:guid}/access",
            "POST /api/v1/identity/users/{id:guid}/unlock",
            "POST /api/v1/identity/users/{id:guid}/deactivate",
            "POST /api/v1/identity/users/{id:guid}/anonymise",
            "POST /api/v1/identity/me/password",
        ], ignoreOrder: true);

    [Theory]
    [MemberData(nameof(Routes))]
    public void Every_endpoint_is_named_documented_and_authorized(string route)
    {
        var endpoint = Endpoints.Single(e => Describe(e) == route);

        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().ShouldNotBeNull(route);
        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary.ShouldNotBeNullOrWhiteSpace(route);
        endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()!.Description.ShouldNotBeNullOrWhiteSpace(route);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty(route);
        endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(p => p.StatusCode)
            .ShouldContain(StatusCodes.Status401Unauthorized, route);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void Only_the_own_password_is_open_to_every_user(string route)
    {
        var policies = Endpoints.Single(e => Describe(e) == route).Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();

        if (route.Contains("/me/", StringComparison.Ordinal))
        {
            policies.ShouldAllBe(p => p == null);
        }
        else
        {
            policies.ShouldContain(AppPolicies.Administration);
        }
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} {endpoint.RoutePattern.RawText}";

    private static List<RouteEndpoint> Map()
    {
        // The module's services are registered (no database is contacted), so handler parameters bind as services; the
        // warehouse queries come from the Inventory module or its HTTP client in a real host.
        var builder = WebApplication.CreateBuilder();
        new IdentityModuleInstaller().AddServices(builder.Services, builder.Configuration);
        builder.Services.AddScoped<IWarehouseQueries>(_ => null!);
        var app = builder.Build();
        new IdentityModuleInstaller().MapEndpoints(app);
        return
        [
            .. ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal)),
        ];
    }
}
