using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Fleet;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Services.Fleet;

/// <summary>Entry point of the Fleet service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class FleetService
{
    // Not static: tests start the host through WebApplicationFactory<FleetService>.
    private FleetService()
    {
    }

    // Hub codes of the machine list and card come from the Inventory service, as the calling user (STAGE_6_PLAN 3.3).
    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new FleetModuleInstaller(), services =>
    {
        services.AddTransient<CallerTokenForwardingHandler>();
        services.AddHttpClient<IWarehouseQueries, HttpWarehouseQueries>(client => client.BaseAddress = new Uri("http://inventory"))
            .AddHttpMessageHandler<CallerTokenForwardingHandler>();
    });
}
