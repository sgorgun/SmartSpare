using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Modules;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// One OpenAPI document per module (ADR 0019, STAGE_4_PLAN section 7), the same in the single host and in each service:
/// <c>/openapi/{module}.json</c>, anonymous. A document describes only the module's versioned business API
/// (<c>/api/v1/{module}/...</c>): no <c>/internal</c> endpoints, no operational endpoints, no servers.
/// </summary>
public static class ApiDocumentation
{
    public const string RoutePattern = "/openapi/{documentName}.json";

    /// <summary>Output-cache and rate-limit policy of the documentation endpoints (ADR 0019 §4).</summary>
    public const string Policy = "api-docs";

    public static string PathOf(string module) => $"/openapi/{module}.json";

    /// <summary>Whether an endpoint at <paramref name="relativePath"/> belongs in <paramref name="module"/>'s document.</summary>
    public static bool IsDocumented(string? relativePath, string module) =>
        relativePath is not null && relativePath.TrimStart('/').StartsWith($"api/v1/{module}/", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddModuleDocuments(this IServiceCollection services, IEnumerable<IModuleInstaller> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var name in modules.Select(module => module.Name))
        {
            services.AddApiDocument(
                name,
                $"SmartSpare {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name)} API",
                "Every operation requires a signed-in session; calls go through the gateway (ADR 0019).",
                path => IsDocumented(path, name));
        }

        return services;
    }

    /// <summary>
    /// Registers one document: the endpoints whose relative path <paramref name="include"/> accepts, with the SmartSpare
    /// conventions (<see cref="OpenApiConventions"/>), version v1 and no servers.
    /// </summary>
    public static IServiceCollection AddApiDocument(
        this IServiceCollection services, string name, string title, string description, Func<string?, bool> include) =>
        services.AddOpenApi(name, options =>
        {
            options.ShouldInclude = endpoint => include(endpoint.RelativePath);
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = title;
                document.Info.Version = "v1";
                document.Info.Description = description;

                // Only the public base URL, which is the page's own origin: no internal service addresses.
                document.Servers?.Clear();
                return Task.CompletedTask;
            });
            OpenApiConventions.Apply(options);
        });

    /// <summary>Maps <c>/openapi/{name}.json</c> for every registered document, anonymous in every environment.</summary>
    public static IEndpointConventionBuilder MapApiDocuments(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapOpenApi(RoutePattern).AllowAnonymous();
    }
}
