using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmartSpare.Infrastructure.Modules;

/// <summary>
/// Entry point of a module. The host discovers implementations at startup, so each module
/// exposes exactly one public type: a sealed installer with a parameterless constructor.
/// </summary>
public interface IModuleInstaller
{
    /// <summary>Lower-case module name, also used as the route prefix (<c>/api/{name}</c>).</summary>
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
