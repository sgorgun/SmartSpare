using Quartz;
using SmartSpare.Infrastructure.Jobs;

namespace SmartSpare.Host;

/// <summary>
/// The scheduler of the single host (#30, ARCHITECTURE_DECISIONS 8): Quartz, in memory, runs the modules' jobs on the cron
/// schedules of <c>Jobs:Schedules</c> (name → Quartz cron expression). Each run goes through <see cref="JobRunner"/>, so it
/// acts as the system user exactly like <c>--job &lt;name&gt;</c> of a service image (ADR 0024). Production has no Quartz:
/// the services topology runs the same jobs as Container Apps cron jobs. <c>Jobs:Enabled = false</c> turns it off (the
/// integration tests run jobs themselves).
/// </summary>
internal static partial class JobScheduling
{
    public const string Section = "Jobs";

    public static IServiceCollection AddJobScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var jobs = configuration.GetSection(Section);
        if (!jobs.GetValue("Enabled", true))
        {
            return services;
        }

        var schedules = jobs.GetSection("Schedules").GetChildren().Where(s => !string.IsNullOrWhiteSpace(s.Value)).ToList();
        services.AddQuartz(quartz =>
        {
            foreach (var schedule in schedules)
            {
                var key = new JobKey(schedule.Key);
                quartz.AddJob<ScheduledJob>(key, job => job.UsingJobData(ScheduledJob.NameKey, schedule.Key));
                quartz.AddTrigger(trigger => trigger.ForJob(key).WithIdentity(schedule.Key + ".trigger").WithCronSchedule(schedule.Value!));
            }
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        return services;
    }

    /// <summary>Runs one registered <see cref="Infrastructure.Jobs.IJob"/> by name; never two runs of a job at once.</summary>
    [DisallowConcurrentExecution]
    internal sealed partial class ScheduledJob(IServiceProvider services, ILogger<ScheduledJob> logger) : Quartz.IJob
    {
        public const string NameKey = "job";

        public async Task Execute(IJobExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var name = context.MergedJobDataMap.GetString(NameKey)!;
            var exitCode = await JobRunner.RunAsync(services, name, context.CancellationToken).ConfigureAwait(false);
            if (exitCode == JobRunner.UnknownJob)
            {
                LogUnknown(name);
            }
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "Jobs:Schedules names {Job}, which no module of this host registers.")]
        private partial void LogUnknown(string job);
    }
}
