using SmartSpare.Modules.Reporting;

namespace SmartSpare.Services.Reporting;

/// <summary>Entry point of the Reporting service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class ReportingService
{
    // Not static: tests start the host through WebApplicationFactory<ReportingService>.
    private ReportingService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new ReportingModuleInstaller());
}
