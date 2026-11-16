using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}
