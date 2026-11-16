using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Modules.Reporting.Contracts;
using SmartSpare.Modules.Reporting.Features.Ping;

namespace SmartSpare.Modules.Reporting;

/// <summary>Registers the Reporting module in the host. The only public type of the module.</summary>
public sealed class ReportingModuleInstaller : IModuleInstaller
{
    public string Name => ReportingModule.Name;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHandlersFromAssembly(typeof(ReportingModuleInstaller).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Reporting");
        PingEndpoint.Map(group);
    }
}
