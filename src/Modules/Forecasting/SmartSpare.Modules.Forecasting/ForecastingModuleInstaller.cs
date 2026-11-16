using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Modules.Forecasting.Contracts;
using SmartSpare.Modules.Forecasting.Features.Ping;

namespace SmartSpare.Modules.Forecasting;

/// <summary>Registers the Forecasting module in the host. The only public type of the module.</summary>
public sealed class ForecastingModuleInstaller : IModuleInstaller
{
    public string Name => ForecastingModule.Name;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddHandlersFromAssembly(typeof(ForecastingModuleInstaller).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Forecasting");
        PingEndpoint.Map(group);
    }
}
