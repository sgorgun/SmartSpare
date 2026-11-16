using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Modules.Procurement.Contracts;
using SmartSpare.Modules.Procurement.Features.Ping;

namespace SmartSpare.Modules.Procurement;

/// <summary>Registers the Procurement module in the host. The only public type of the module.</summary>
public sealed class ProcurementModuleInstaller : IModuleInstaller
{
    public string Name => ProcurementModule.Name;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHandlersFromAssembly(typeof(ProcurementModuleInstaller).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Procurement");
        PingEndpoint.Map(group);
    }
}
