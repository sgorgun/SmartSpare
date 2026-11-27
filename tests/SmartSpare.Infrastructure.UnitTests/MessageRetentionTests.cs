using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure;

/// <summary>The retention of delivered integration events (#30): the settings' rules and the batching of the job.</summary>
public sealed class MessageRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_defaults_keep_the_inbox_far_longer_than_any_redelivery()
    {
        var outbox = new OutboxOptions();

        MessageRetentionOptions.RedeliveryWindow(outbox).ShouldBe(TimeSpan.FromMinutes(120), "10 attempts × 10 min cap × 1.2 jitter");
        new MessageRetentionOptions().Validate(outbox).ShouldBeEmpty();
    }

    [Fact]
    public void An_inbox_shorter_than_the_redelivery_window_is_refused()
    {
        var slowOutbox = new OutboxOptions { MaxAttempts = 100, BackoffCap = TimeSpan.FromHours(12) };   // up to 60 days of retries

        new MessageRetentionOptions { InboxDays = 30 }.Validate(slowOutbox).ShouldHaveSingleItem().ShouldContain("InboxDays (30)");
        new MessageRetentionOptions { InboxDays = 61 }.Validate(slowOutbox).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(7, 0)]
    [InlineData(7, 100_001)]
    public void Outbox_days_and_batch_size_must_be_positive(int outboxDays, int batch) =>
        new MessageRetentionOptions { OutboxDays = outboxDays, BatchSize = batch }.Validate(new OutboxOptions()).ShouldHaveSingleItem();

    [Fact]
    public async Task The_job_purges_every_outbox_with_the_cut_offs_until_a_batch_comes_back_short()
    {
        var fleet = new FakeStore("fleet", [new(2, 2), new(2, 1), new(1, 0)]);
        var iam = new FakeStore("iam", [new(0, 0)]);
        var job = new MessageRetentionJob(
            new EmptyServices(), [fleet, iam], new FakeTimeProvider(Now),
            Options.Create(new MessageRetentionOptions { OutboxDays = 7, InboxDays = 30, BatchSize = 2 }),
            NullLogger<MessageRetentionJob>.Instance);

        await job.RunAsync(CancellationToken.None);

        job.Name.ShouldBe("outbox-retention");
        fleet.Calls.Count.ShouldBe(3, "a full batch of either table asks again");
        fleet.Calls.ShouldAllBe(c => c.OutboxBefore == Now.AddDays(-7) && c.InboxBefore == Now.AddDays(-30) && c.Batch == 2);
        iam.Calls.ShouldHaveSingleItem();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeStore(string schema, Queue<MessagePurge> answers) : IOutboxStore
    {
        public FakeStore(string schema, MessagePurge[] answers)
            : this(schema, new Queue<MessagePurge>(answers))
        {
        }

        public string Schema { get; } = schema;

        public List<(DateTimeOffset OutboxBefore, DateTimeOffset InboxBefore, int Batch)> Calls { get; } = [];

        public Task<MessagePurge> PurgeAsync(
            IServiceProvider scopedServices, DateTimeOffset outboxBefore, DateTimeOffset inboxBefore, int batch, CancellationToken cancellationToken)
        {
            Calls.Add((outboxBefore, inboxBefore, batch));
            return Task.FromResult(answers.Dequeue());
        }

        public Task<int> DispatchBatchAsync(
            IServiceProvider scopedServices, Func<OutboxEnvelope, CancellationToken, Task> publish, OutboxOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OutboxStatus> GetStatusAsync(IServiceProvider scopedServices, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
