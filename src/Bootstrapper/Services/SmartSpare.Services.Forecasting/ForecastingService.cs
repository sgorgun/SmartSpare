using SmartSpare.Modules.Forecasting;

namespace SmartSpare.Services.Forecasting;

/// <summary>Entry point of the Forecasting service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class ForecastingService
{
    // Not static: tests start the host through WebApplicationFactory<ForecastingService>.
    private ForecastingService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new ForecastingModuleInstaller());
}
