namespace SmartSpare.SharedKernel;

/// <summary>
/// A fact one module publishes for others (ADR 0003). Defined in the publisher's <c>*.Contracts</c>
/// project, written to the publisher's outbox in the same transaction as the data change, and
/// delivered at least once; receivers deduplicate by <see cref="Id"/> through their inbox.
/// </summary>
public interface IIntegrationEvent
{
    Guid Id { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>Convenience base record: a new id and the current time unless set explicitly.</summary>
public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
