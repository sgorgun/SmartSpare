using SmartSpare.Modules.Procurement;

namespace SmartSpare.Services.Procurement;

/// <summary>Entry point of the Procurement service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class ProcurementService
{
    // Not static: tests start the host through WebApplicationFactory<ProcurementService>.
    private ProcurementService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new ProcurementModuleInstaller());
}
