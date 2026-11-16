using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Forecasting.Contracts;
using SmartSpare.Modules.Forecasting.Infrastructure.Persistence;
using SmartSpare.Modules.Forecasting.Features.Ping;

namespace SmartSpare.Modules.Forecasting;

/// <summary>Registers the Forecasting module in the host. The only public type of the module.</summary>
public sealed class ForecastingModuleInstaller : IModuleInstaller
{
    public string Name => ForecastingModule.Name;

    public IModuleDatabase Database => ForecastingDatabase.Instance;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(ForecastingModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        ForecastingDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), ForecastingDbContext.HistorySchema);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Forecasting");
        PingEndpoint.Map(group);
    }
}
