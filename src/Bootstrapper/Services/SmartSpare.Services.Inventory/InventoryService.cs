using SmartSpare.Modules.Inventory;

namespace SmartSpare.Services.Inventory;

/// <summary>Entry point of the Inventory service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class InventoryService
{
    // Not static: tests start the host through WebApplicationFactory<InventoryService>.
    private InventoryService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new InventoryModuleInstaller());
}
