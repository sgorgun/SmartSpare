using SmartSpare.Modules.Fleet;

namespace SmartSpare.Services.Fleet;

/// <summary>Entry point of the Fleet service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class FleetService
{
    // Not static: tests start the host through WebApplicationFactory<FleetService>.
    private FleetService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new FleetModuleInstaller());
}
