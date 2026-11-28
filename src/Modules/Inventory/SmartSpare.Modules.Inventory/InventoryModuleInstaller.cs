using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;
using SmartSpare.Modules.Inventory.Features.GetPart;
using SmartSpare.Modules.Inventory.Features.Imports;
using SmartSpare.Modules.Inventory.Features.PartCatalog;
using SmartSpare.Modules.Inventory.Features.ServiceKits;
using SmartSpare.Modules.Inventory.Features.ListWarehouses;
using SmartSpare.Modules.Inventory.Features.Ping;
using SmartSpare.Modules.Inventory.Features.SearchParts;
using SmartSpare.Modules.Inventory.Features.Stock;
using SmartSpare.Modules.Inventory.Features.Transfers;
using SmartSpare.Modules.Inventory.Features.Warehouses;

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
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IWarehouseQueries, WarehouseQueries>();
        services.AddScoped<TransferAccess>();
        services.AddScoped<TransferDetailsReader>();
        services.AddScoped<TransferFunctions>();
        services.AddScoped<ImportStore>();
        services.AddScoped<ServiceKitStore>();
        services.TryAddScoped<SmartSpare.Infrastructure.MasterData.IActorNames, Infrastructure.Queries.EmployeeActorNames>();
        services.AddImportRateLimit();
        services.AddCatalogCsvRateLimit();
        services.AddJob<PartCatalogPublishJob>();
        services.AddJob<Features.Brands.BrandCatalogPublishJob>();
        services.AddJob<ReservationExpiryJob>();
        services.AddJob<Features.Responsibilities.ResponsibilityPublishJob>();
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
        StockOverviewEndpoint.Map(v1);
        GetStockPositionEndpoint.Map(v1);
        ChangeStockLevelsEndpoint.Map(v1);
        ListMovementsEndpoint.Map(v1);
        AdjustStockEndpoint.Map(v1);
        TransferQueryEndpoints.Map(v1);
        TransferCommandEndpoints.Map(v1);
        ImportEndpoints.Map(v1);
        Features.Responsibilities.ResponsibleEndpoints.Map(v1);
        Features.AdjustmentReasons.AdjustmentReasonCatalog.Map(v1);
    }

    public void MapInternalEndpoints(IEndpointRouteBuilder endpoints) => WarehouseQueriesEndpoint.Map(endpoints);
}
