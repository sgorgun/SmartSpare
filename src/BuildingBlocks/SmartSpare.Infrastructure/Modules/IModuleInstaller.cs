using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.Modules;

/// <summary>
/// Entry point of a module. The host lists the installers it runs explicitly, and each module
/// exposes exactly one public type: its sealed installer.
/// </summary>
public interface IModuleInstaller
{
    /// <summary>Lower-case module name, also used as the route prefix (<c>/api/{name}</c>).</summary>
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);

    /// <summary>
    /// Endpoints for other services under <c>/internal/v1/{name}</c> (ADR 0015 §4): Contracts queries over HTTP. The
    /// gateway never routes them; each sets its own policy (<c>GatewayOnly</c> or a user policy). Mapped in both
    /// topologies, so the HTTP clients can be tested against the single host too.
    /// </summary>
    void MapInternalEndpoints(IEndpointRouteBuilder endpoints)
    {
    }

    /// <summary>
    /// The module's migrations, or <see langword="null"/> while the module has no DbContext.
    /// The migration runner applies them in catalogue order (ModuleCatalog).
    /// </summary>
    IModuleDatabase? Database => null;
}
