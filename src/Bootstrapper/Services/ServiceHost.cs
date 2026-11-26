using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.RabbitMq;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Services;

/// <summary>
/// The composition root of every <c>SmartSpare.Services.&lt;Module&gt;</c> host (ADR 0015, STAGE_4_PLAN section 3), linked
/// into each service project. One module, the RabbitMQ transport, internal-token authentication only, no Blazor, no
/// migrations at start-up. <c>--job &lt;name&gt;</c> runs one job and exits instead of serving HTTP.
/// </summary>
internal static class ServiceHost
{
    /// <summary>
    /// Connections per replica (STAGE_4_PLAN 8.5): six services share one Burstable server, so each pools at most four
    /// unless the connection string sets its own limit.
    /// </summary>
    public const int MaxPoolSize = 4;

    /// <param name="args">Command line; <c>--job &lt;name&gt;</c> selects job mode.</param>
    /// <param name="module">The service's module.</param>
    /// <param name="configure">More registrations of this service only, for example a job that lives outside the module.</param>
    public static WebApplication Build(string[] args, IModuleInstaller module, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var builder = WebApplication.CreateBuilder(args);
        LimitConnectionPool(builder.Configuration);

        builder.AddServiceDefaults();
        builder.Services.AddSmartSpareProblemDetails();

        builder.Services.AddRabbitMqTransport(builder.Configuration, $"smartspare-{module.Name}");
        builder.Services.AddModules(builder.Configuration, [module], Topology.Services);
        builder.Services.AddModuleDocuments([module]);
        builder.Services.AddServiceAuthentication();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseSmartSpareProblemDetails();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapDefaultEndpoints();
        app.MapModules([module]);

        // Anonymous, like everywhere (ADR 0019); the gateway proxies it as /openapi/{module}.json and caches it.
        app.MapApiDocuments();
        return app;
    }

    public static async Task<int> RunAsync(string[] args, IModuleInstaller module, Action<IServiceCollection>? configure = null)
    {
        var app = Build(args, module, configure);
        await using (app.ConfigureAwait(false))
        {
            if (JobRunner.JobName(args) is { } job)
            {
                return await RunJobAsync(app, job).ConfigureAwait(false);
            }

            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
    }

    /// <summary>
    /// Job mode never starts the host, and the OpenTelemetry providers are normally created by a hosted service at start.
    /// So they are created here, before the job, and flushed after it: a job's metrics, traces and logs leave the process
    /// before it exits (a cron job lives for seconds).
    /// </summary>
    private static async Task<int> RunJobAsync(WebApplication app, string job)
    {
        var meters = app.Services.GetService<MeterProvider>();
        var tracers = app.Services.GetService<TracerProvider>();
        var loggers = app.Services.GetService<LoggerProvider>();

        var exitCode = await JobRunner.RunAsync(app.Services, job, app.Lifetime.ApplicationStopping).ConfigureAwait(false);

        const int FlushTimeoutMilliseconds = 10_000;
        meters?.ForceFlush(FlushTimeoutMilliseconds);
        tracers?.ForceFlush(FlushTimeoutMilliseconds);
        loggers?.ForceFlush(FlushTimeoutMilliseconds);
        return exitCode;
    }

    private static void LimitConnectionPool(ConfigurationManager configuration)
    {
        var key = $"ConnectionStrings:{PersistenceRegistration.ConnectionStringName}";
        if (configuration[key] is { Length: > 0 } connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!connectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase)
                && !connectionString.Contains("MaxPoolSize", StringComparison.OrdinalIgnoreCase))
            {
                builder.MaxPoolSize = MaxPoolSize;
                configuration[key] = builder.ConnectionString;
            }
        }
    }
}
