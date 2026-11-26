using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Identity;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Services.Identity;

/// <summary>Entry point of the Identity service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class IdentityService
{
    // Not static: tests start the host through WebApplicationFactory<IdentityService>.
    private IdentityService()
    {
    }

    // User administration validates home hubs with the Inventory service, as the calling administrator (#46).
    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new IdentityModuleInstaller(), services =>
    {
        services.AddTransient<CallerTokenForwardingHandler>();
        services.AddHttpClient<IWarehouseQueries, HttpWarehouseQueries>(client => client.BaseAddress = new Uri("http://inventory"))
            .AddHttpMessageHandler<CallerTokenForwardingHandler>();
    });
}
