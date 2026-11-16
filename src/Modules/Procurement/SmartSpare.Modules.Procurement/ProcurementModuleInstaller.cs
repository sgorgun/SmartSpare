using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Procurement.Contracts;
using SmartSpare.Modules.Procurement.Infrastructure.Persistence;
using SmartSpare.Modules.Procurement.Features.Ping;

namespace SmartSpare.Modules.Procurement;

/// <summary>Registers the Procurement module in the host. The only public type of the module.</summary>
public sealed class ProcurementModuleInstaller : IModuleInstaller
{
    public string Name => ProcurementModule.Name;

    public IModuleDatabase Database => ProcurementDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(ProcurementModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        ProcurementDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), ProcurementDbContext.HistorySchema);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Procurement");
        PingEndpoint.Map(group);
    }
}
