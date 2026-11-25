using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Features.GetPart;
using SmartSpare.Modules.Inventory.Features.ListWarehouses;
using SmartSpare.Modules.Inventory.Features.Ping;
using SmartSpare.Modules.Inventory.Features.SearchParts;

namespace SmartSpare.Modules.Inventory;

/// <summary>Registers the Inventory module in the host. The only public type of the module.</summary>
public sealed class InventoryModuleInstaller : IModuleInstaller
{
    public string Name => InventoryModule.Name;

    public IModuleDatabase Database => InventoryDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(InventoryModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        InventoryDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), InventoryDbContext.HistorySchema);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Inventory");
        PingEndpoint.Map(group);

        // The versioned business API (ADR 0012), described in the module's OpenAPI document (ADR 0019).
        var v1 = endpoints.MapGroup($"/api/v1/{Name}").WithTags("Inventory");
        SearchPartsEndpoint.Map(v1);
        GetPartEndpoint.Map(v1);
        ListWarehousesEndpoint.Map(v1);
    }
}
