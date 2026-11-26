using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>The <c>--job &lt;name&gt;</c> entry point of the service images (STAGE_4_PLAN section 3).</summary>
public sealed class JobRunnerTests
{
    [Theory]
    [InlineData(new[] { "--job", "reporting.broker-metrics" }, "reporting.broker-metrics")]
    [InlineData(new[] { "--urls", "http://+:8080", "--job=reporting.broker-metrics" }, "reporting.broker-metrics")]
    [InlineData(new[] { "--urls", "http://+:8080" }, null)]
    [InlineData(new[] { "--job" }, null)]
    public void The_job_name_comes_from_the_arguments(string[] args, string? expected) =>
        JobRunner.JobName(args).ShouldBe(expected);

    [Fact]
    public async Task A_known_job_runs_once_in_its_own_scope()
    {
        await using var services = Services(new RecordingJob("test.ok", fail: false));

        var exitCode = await JobRunner.RunAsync(services, "test.ok", TestContext.Current.CancellationToken);

        exitCode.ShouldBe(JobRunner.Succeeded);
        RecordingJob.Runs.ShouldContain("test.ok");
    }

    [Fact]
    public async Task A_job_acts_as_the_named_system_user_without_roles()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddScoped<ActingUser>();
        services.AddScoped<IJob>(sp => new ActingUserJob(sp.GetRequiredService<ActingUser>()));
        await using var provider = services.BuildServiceProvider();

        (await JobRunner.RunAsync(provider, ActingUserJob.JobName, TestContext.Current.CancellationToken)).ShouldBe(JobRunner.Succeeded);

        ActingUserJob.Seen.ShouldBe((SystemUser.Id, $"job:{ActingUserJob.JobName}"));
    }

    [Fact]
    public async Task A_failing_job_exits_with_failed()
    {
        await using var services = Services(new RecordingJob("test.fails", fail: true));

        (await JobRunner.RunAsync(services, "test.fails", TestContext.Current.CancellationToken)).ShouldBe(JobRunner.Failed);
    }

    [Fact]
    public async Task An_unknown_job_exits_with_unknown_job()
    {
        await using var services = Services(new RecordingJob("test.ok", fail: false));

        (await JobRunner.RunAsync(services, "test.missing", TestContext.Current.CancellationToken)).ShouldBe(JobRunner.UnknownJob);
    }

    private static ServiceProvider Services(RecordingJob job)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddScoped<IJob>(_ => job);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingJob(string name, bool fail) : IJob
    {
        public static List<string> Runs { get; } = [];

        public string Name => name;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(name);
            }

            return fail ? throw new InvalidOperationException("The job failed (test).") : Task.CompletedTask;
        }
    }

    private sealed class ActingUserJob(ActingUser acting) : IJob
    {
        public const string JobName = "test.acting-user";

        public static (Guid? UserId, string? Reason) Seen { get; private set; }

        public string Name => JobName;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            Seen = (acting.UserId, acting.Reason);
            return Task.CompletedTask;
        }
    }
}
