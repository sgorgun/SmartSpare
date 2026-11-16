using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Features.Ping;

namespace SmartSpare.Modules.Inventory;

/// <summary>Registers the Inventory module in the host. The only public type of the module.</summary>
public sealed class InventoryModuleInstaller : IModuleInstaller
{
    public string Name => InventoryModule.Name;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHandlersFromAssembly(typeof(InventoryModuleInstaller).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Inventory");
        PingEndpoint.Map(group);
    }
}
