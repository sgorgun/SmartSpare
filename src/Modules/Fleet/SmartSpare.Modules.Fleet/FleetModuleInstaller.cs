using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Features.Machines;
using SmartSpare.Modules.Fleet.Features.Maintenance;
using SmartSpare.Modules.Fleet.Features.Readings;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Infrastructure.Queries;
using SmartSpare.Modules.Fleet.Features.Ping;

namespace SmartSpare.Modules.Fleet;

/// <summary>Registers the Fleet module in the host. The only public type of the module.</summary>
public sealed class FleetModuleInstaller : IModuleInstaller
{
    public string Name => FleetModule.Name;

    public IModuleDatabase Database => FleetDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(FleetModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<FleetDueReader>();
        services.AddScoped<ReadingStore>();
        FleetDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), FleetDbContext.HistorySchema);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Fleet");
        PingEndpoint.Map(group);

        // The versioned business API (ADR 0012), described in the module's OpenAPI document (ADR 0019).
        var v1 = endpoints.MapGroup($"/api/v1/{Name}").WithTags("Fleet");
        MachineQueryEndpoints.Map(v1);
        MaintenanceQueryEndpoints.Map(v1);
        RecordReadingEndpoint.Map(v1);
    }
}
