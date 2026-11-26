using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.Jobs;

/// <summary>
/// One unit of scheduled work (STAGE_4_PLAN section 3): Quartz runs it in the single host (stage 5), and in production
/// the owning service's image runs it as a Container Apps job with <c>--job &lt;name&gt;</c>. Job code is the same.
/// </summary>
public interface IJob
{
    /// <summary>Stable, lower-case name, prefixed with the module: <c>reporting.broker-metrics</c>.</summary>
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

public static class JobRegistration
{
    /// <summary>Registers a job; a module calls it from <c>AddServices</c>.</summary>
    public static IServiceCollection AddJob<TJob>(this IServiceCollection services)
        where TJob : class, IJob
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IJob, TJob>();
        return services;
    }
}

/// <summary>The <c>--job &lt;name&gt;</c> entry point of a service image.</summary>
public static class JobRunner
{
    public const int Succeeded = 0;

    public const int Failed = 1;

    public const int UnknownJob = 2;

    /// <summary>The value of <c>--job &lt;name&gt;</c> or <c>--job=&lt;name&gt;</c>, or null.</summary>
    public static string? JobName(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--job" && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--job=", StringComparison.Ordinal))
            {
                return args[i]["--job=".Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the named job in its own scope and returns the process exit code: <see cref="Succeeded"/>,
    /// <see cref="Failed"/> (the exception is logged) or <see cref="UnknownJob"/>.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var jobs = scope.ServiceProvider.GetServices<IJob>().ToList();
            var job = jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.Ordinal));
            if (job is null)
            {
                await Console.Error.WriteLineAsync(
                    $"Unknown job '{name}'. Jobs of this service: {(jobs.Count == 0 ? "none" : string.Join(", ", jobs.Select(j => j.Name)))}.")
                    .ConfigureAwait(false);
                return UnknownJob;
            }

            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SmartSpare.Jobs");

            // ADR 0024: a job acts as the named system user, without roles, so its writes are attributed to it.
            using var actingAs = scope.ServiceProvider.GetService<ActingUser>()?.ActAs(SystemUser.Id, "job:" + job.Name);
            try
            {
                await job.RunAsync(cancellationToken).ConfigureAwait(false);
                return Succeeded;
            }
#pragma warning disable CA1031 // The job's exit code tells Container Apps it failed; the log and trace say why.
            catch (Exception ex)
#pragma warning restore CA1031
            {
#pragma warning disable CA1848, CA2254 // One message per failed job run.
                logger.LogError(ex, "Job {Job} failed.", job.Name);
#pragma warning restore CA1848, CA2254
                return Failed;
            }
        }
    }
}
