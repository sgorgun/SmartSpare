using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
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

    public static WebApplication Build(string[] args, IModuleInstaller module)
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

    public static async Task<int> RunAsync(string[] args, IModuleInstaller module)
    {
        var app = Build(args, module);
        await using (app.ConfigureAwait(false))
        {
            if (JobRunner.JobName(args) is { } job)
            {
                return await JobRunner.RunAsync(app.Services, job, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
            }

            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
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
