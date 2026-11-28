using SmartSpare.Modules.Fleet;

namespace SmartSpare.Services.Fleet;

/// <summary>Entry point of the Fleet service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class FleetService
{
    // Not static: tests start the host through WebApplicationFactory<FleetService>.
    private FleetService()
    {
    }

    // Hubs come from the module's own warehouse copy, fed by WarehouseCatalogChanged (ADR 0029): no call to Inventory.
    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new FleetModuleInstaller());
}
