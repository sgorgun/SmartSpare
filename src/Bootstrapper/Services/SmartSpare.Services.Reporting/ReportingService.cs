using SmartSpare.Infrastructure.RabbitMq;
using SmartSpare.Modules.Reporting;

namespace SmartSpare.Services.Reporting;

/// <summary>
/// Entry point of the Reporting service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>. Its image also
/// runs the broker metrics job, <c>--job reporting.broker-metrics</c> (STAGE_4_PLAN 6.2).
/// </summary>
public sealed class ReportingService
{
    // Not static: tests start the host through WebApplicationFactory<ReportingService>.
    private ReportingService()
    {
    }

    public static Task<int> Main(string[] args) =>
        ServiceHost.RunAsync(args, new ReportingModuleInstaller(), services => services.AddBrokerMetricsJob());
}
