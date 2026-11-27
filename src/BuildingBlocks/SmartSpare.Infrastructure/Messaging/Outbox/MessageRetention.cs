using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSpare.Infrastructure.Jobs;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// How long delivered integration events stay in the module tables (#30, STAGE_6_PLAN 8), section
/// <c>Messaging:Retention</c>. The inbox must outlive every possible redelivery, or a late duplicate would be handled
/// twice: start-up fails when <see cref="InboxDays"/> is not longer than the outbox's longest retry window.
/// </summary>
public sealed class MessageRetentionOptions
{
    public const string Section = "Messaging:Retention";

    /// <summary>Days a delivered outbox row is kept (for tracing a delivery); default 7.</summary>
    public int OutboxDays { get; set; } = 7;

    /// <summary>Days a handled inbox row is kept (the idempotency record); default 30.</summary>
    public int InboxDays { get; set; } = 30;

    /// <summary>Rows deleted per table and call; default 5,000.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>
    /// The longest time between the first and the last delivery attempt of one event: every attempt waits at most the
    /// backoff cap plus its jitter (<see cref="OutboxBackoff"/>).
    /// </summary>
    public static TimeSpan RedeliveryWindow(OutboxOptions outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return outbox.BackoffCap * outbox.MaxAttempts * (1 + OutboxBackoff.Jitter);
    }

    /// <summary>The problems of the settings, empty when they are valid.</summary>
    public IReadOnlyList<string> Validate(OutboxOptions outbox)
    {
        var problems = new List<string>();
        if (OutboxDays < 1)
        {
            problems.Add($"{Section}:OutboxDays must be at least 1.");
        }

        if (BatchSize is < 1 or > 100_000)
        {
            problems.Add($"{Section}:BatchSize must be between 1 and 100,000.");
        }

        if (TimeSpan.FromDays(InboxDays) <= RedeliveryWindow(outbox))
        {
            problems.Add($"{Section}:InboxDays ({InboxDays}) must be longer than the redelivery window of {RedeliveryWindow(outbox)}, "
                + "or a late redelivery would be handled twice.");
        }

        return problems;
    }
}

/// <summary>
/// The job <c>outbox-retention</c> (#30): for every outbox of this host's modules, deletes delivered outbox rows and
/// handled inbox rows older than the retention in batches until a batch comes back short. Daily from Quartz in the
/// single host; <c>--job outbox-retention</c> of each service image in production. Acts as the system user (ADR 0024).
/// </summary>
public sealed partial class MessageRetentionJob(
    IServiceProvider services,
    IEnumerable<IOutboxStore> stores,
    TimeProvider time,
    IOptions<MessageRetentionOptions> options,
    ILogger<MessageRetentionJob> logger) : IJob
{
    public const string JobName = "outbox-retention";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var outboxBefore = now.AddDays(-settings.OutboxDays);
        var inboxBefore = now.AddDays(-settings.InboxDays);
        foreach (var store in stores)
        {
            int outbox = 0, inbox = 0;
            MessagePurge purge;
            do
            {
                purge = await store.PurgeAsync(services, outboxBefore, inboxBefore, settings.BatchSize, cancellationToken).ConfigureAwait(false);
                outbox += purge.OutboxDeleted;
                inbox += purge.InboxDeleted;
            }
            while (purge.OutboxDeleted == settings.BatchSize || purge.InboxDeleted == settings.BatchSize);

            LogPurged(store.Schema, outbox, inbox);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Message retention {Schema}: deleted {Outbox} delivered outbox and {Inbox} handled inbox row(s).")]
    private partial void LogPurged(string schema, int outbox, int inbox);
}

public static class MessageRetentionRegistration
{
    /// <summary>Registers the job and its settings, validated when the host starts.</summary>
    public static IServiceCollection AddMessageRetention(this IServiceCollection services)
    {
        services.AddOptions<MessageRetentionOptions>()
            .BindConfiguration(MessageRetentionOptions.Section)
            .Validate<IOptions<OutboxOptions>>((retention, outbox) => retention.Validate(outbox.Value).Count == 0,
                "Messaging:Retention is invalid: InboxDays must exceed the redelivery window, OutboxDays and BatchSize must be positive.")
            .ValidateOnStart();
        services.AddJob<MessageRetentionJob>();
        return services;
    }
}
