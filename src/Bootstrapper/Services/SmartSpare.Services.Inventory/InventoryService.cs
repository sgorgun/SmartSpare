using Microsoft.Extensions.DependencyInjection;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Inventory;

namespace SmartSpare.Services.Inventory;

/// <summary>Entry point of the Inventory service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class InventoryService
{
    // Not static: tests start the host through WebApplicationFactory<InventoryService>.
    private InventoryService()
    {
    }

    // Responsible employees are validated and named by the Identity service, as the calling user (ADR 0026).
    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new InventoryModuleInstaller(), services =>
    {
        services.AddTransient<CallerTokenForwardingHandler>();
        services.AddHttpClient<IEmployeeDirectory, HttpEmployeeDirectory>(client => client.BaseAddress = new Uri("http://identity"))
            .AddHttpMessageHandler<CallerTokenForwardingHandler>();
    });
}
