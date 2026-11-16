using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Reporting.Contracts;
using SmartSpare.Modules.Reporting.Infrastructure.Persistence;
using SmartSpare.Modules.Reporting.Features.Ping;

namespace SmartSpare.Modules.Reporting;

/// <summary>Registers the Reporting module in the host. The only public type of the module.</summary>
public sealed class ReportingModuleInstaller : IModuleInstaller
{
    public string Name => ReportingModule.Name;

    public IModuleDatabase Database => ReportingDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(ReportingModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        ReportingDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), outboxSchema: null);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Reporting");
        PingEndpoint.Map(group);
    }
}
