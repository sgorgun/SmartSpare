using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Features.Ping;

namespace SmartSpare.Modules.Identity;

/// <summary>Registers the Identity module in the host. The only public type of the module.</summary>
public sealed class IdentityModuleInstaller : IModuleInstaller
{
    public string Name => IdentityModule.Name;

    public IModuleDatabase Database => IdentityDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHandlersFromAssembly(typeof(IdentityModuleInstaller).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Identity");
        PingEndpoint.Map(group);
    }
}
