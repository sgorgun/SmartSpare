using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quartz;
using Quartz.Impl.Matchers;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Retention of delivered integration events (#30) on the EF-built database: in every module schema with an outbox, the job
/// <c>outbox-retention</c> deletes delivered outbox rows older than 7 days and handled inbox rows older than 30 days, and
/// keeps recent rows and every undelivered row, parked ones included. The application role can run the purge function but
/// cannot delete from the tables. The single host's scheduler plans the configured jobs, all of which exist.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class MessageRetentionIntegrationTests(IdentityDatabaseFixture database)
{
    private static readonly string[] Schemas = ["iam", "inventory", "fleet", "procurement", "forecast"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Old_delivered_messages_are_purged_in_every_module_and_everything_else_stays()
    {
        await using var host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var now = host.Time.GetUtcNow();
        var tag = Guid.NewGuid().ToString("N")[..8];
        foreach (var schema in Schemas)
        {
            await SeedAsync(schema, tag, now);
        }

        (await JobRunner.RunAsync(host.Services, MessageRetentionJob.JobName, Ct)).ShouldBe(JobRunner.Succeeded);

        foreach (var schema in Schemas)
        {
            (await OutboxLeftAsync(schema, tag)).ShouldBe(["new-done", "old-parked", "old-pending"], $"{schema}.outbox_message");
            (await InboxLeftAsync(schema, tag)).ShouldBe(["new"], $"{schema}.inbox_message");
        }
    }

    [Fact]
    public async Task Small_batches_repeat_until_everything_old_is_gone()
    {
        await using var host = new HostFactory { ConnectionString = database.ApiConnectionString };
        var now = host.Time.GetUtcNow();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync("fleet", $"{tag}{i}", now);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var job = new MessageRetentionJob(
            scope.ServiceProvider,
            scope.ServiceProvider.GetServices<IOutboxStore>().Where(s => s.Schema == "fleet"),
            host.Time,
            Microsoft.Extensions.Options.Options.Create(new MessageRetentionOptions { BatchSize = 2 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageRetentionJob>.Instance);
        await job.RunAsync(Ct);

        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM fleet.outbox_message WHERE type LIKE @t AND processed_at < now() - interval '7 days'", ("t", $"Retention.{tag}%")))
            .ShouldBe(0, "five old delivered rows, deleted two at a time");
    }

    [Fact]
    public async Task The_application_role_purges_only_through_the_function()
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var delete = new NpgsqlCommand("DELETE FROM fleet.outbox_message WHERE false", connection);

        var refused = await Should.ThrowAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        await using var purge = new NpgsqlCommand("SELECT * FROM fleet.purge_processed_messages(now() - interval '7 days', now() - interval '30 days', 1)", connection);
        await purge.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task The_single_host_schedules_the_configured_jobs_and_each_of_them_exists()
    {
        await using var host = new HostFactory { ConnectionString = database.ApiConnectionString, WithJobScheduler = true };
        _ = host.Services;   // starts the host and its scheduler

        var scheduler = await host.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(Ct);
        var triggers = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup(), Ct);
        var planned = triggers.Select(t => t.Name.Replace(".trigger", string.Empty, StringComparison.Ordinal)).Order().ToList();
        planned.ShouldBe(["inventory.reservation-expiry", MessageRetentionJob.JobName]);

        await using var scope = host.Services.CreateAsyncScope();
        var registered = scope.ServiceProvider.GetServices<SmartSpare.Infrastructure.Jobs.IJob>().Select(j => j.Name).ToList();
        planned.ShouldAllBe(name => registered.Contains(name));
    }

    private Task SeedAsync(string schema, string tag, DateTimeOffset now) =>
        database.ExecuteAsync($"""
            INSERT INTO {schema}.outbox_message (id, occurred_at, type, payload, processed_at, attempts, exhausted_at) VALUES
              (gen_random_uuid(), @now - interval '20 days', 'Retention.{tag}.old-done', jsonb_build_object(), @now - interval '10 days', 0, NULL),
              (gen_random_uuid(), @now - interval '2 days', 'Retention.{tag}.new-done', jsonb_build_object(), @now - interval '1 day', 0, NULL),
              (gen_random_uuid(), @now - interval '40 days', 'Retention.{tag}.old-pending', jsonb_build_object(), NULL, 1, NULL),
              (gen_random_uuid(), @now - interval '40 days', 'Retention.{tag}.old-parked', jsonb_build_object(), NULL, 10, @now - interval '39 days');
            INSERT INTO {schema}.inbox_message (message_id, consumer, processed_at) VALUES
              (gen_random_uuid(), 'retention.{tag}.old', @now - interval '40 days'),
              (gen_random_uuid(), 'retention.{tag}.new', @now - interval '5 days');
            """,
            ("now", now));

    private async Task<string[]> OutboxLeftAsync(string schema, string tag) =>
        (await database.ScalarAsync<string>(
            $"SELECT coalesce(string_agg(substr(type, length(@p) + 1), ',' ORDER BY type), '') FROM {schema}.outbox_message WHERE type LIKE @p || '%'",
            ("p", $"Retention.{tag}."))).Split(',', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string[]> InboxLeftAsync(string schema, string tag) =>
        (await database.ScalarAsync<string>(
            $"SELECT coalesce(string_agg(substr(consumer, length(@p) + 1), ',' ORDER BY consumer), '') FROM {schema}.inbox_message WHERE consumer LIKE @p || '%'",
            ("p", $"retention.{tag}."))).Split(',', StringSplitOptions.RemoveEmptyEntries);
}
