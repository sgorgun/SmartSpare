using SmartSpare.Modules.Identity;

namespace SmartSpare.Services.Identity;

/// <summary>Entry point of the Identity service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class IdentityService
{
    // Not static: tests start the host through WebApplicationFactory<IdentityService>.
    private IdentityService()
    {
    }

    // Hubs come from the module's own warehouse copy, fed by WarehouseCatalogChanged (ADR 0029): no call to Inventory.
    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new IdentityModuleInstaller());
}
