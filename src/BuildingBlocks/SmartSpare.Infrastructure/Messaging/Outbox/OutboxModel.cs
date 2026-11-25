using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Messaging.Outbox;

/// <summary>
/// An integration event waiting in the publishing module's <c>&lt;schema&gt;.outbox_message</c>.
/// Written in the same transaction (and the same SaveChanges) as the data change (ADR 0003).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>CLR type of the event: <c>Namespace.Type, Assembly</c>.</summary>
    public string Type { get; set; } = null!;

    /// <summary>The event serialized as a JSON object.</summary>
    public string Payload { get; set; } = null!;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>Earliest time of the next delivery attempt after a failure (exponential back-off, #31); NULL = now.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Time the message ran out of attempts and was parked for an operator (#31); NULL = still being delivered.</summary>
    public DateTimeOffset? ExhaustedAt { get; set; }
}

/// <summary>
/// A message a consumer has already handled, in the consuming module's <c>&lt;schema&gt;.inbox_message</c>.
/// Delivery is at least once; the inbox row, written in the handler's transaction, makes a redelivery a no-op.
/// </summary>
public sealed class InboxMessage
{
    public Guid MessageId { get; set; }

    /// <summary>The handler type that consumed the message (several modules may consume one event).</summary>
    public string Consumer { get; set; } = null!;

    public DateTimeOffset ProcessedAt { get; set; }
}

public static class OutboxModelBuilderExtensions
{
    public const int MaxErrorLength = 2000;

    /// <summary>Maps <c>outbox_message</c> and <c>inbox_message</c> into the module's own schema.</summary>
    public static ModelBuilder AddOutboxAndInbox(this ModelBuilder modelBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            // jsonb is justified like audit_log's: one table for every event type of the module; the
            // payload is a typed event (the Type column names it), read only by the dispatcher.
            e.ToTable("outbox_message", schema, t =>
            {
                t.HasComment("Integration events of this module waiting for delivery (transactional outbox, ADR 0003).");
                t.HasCheckConstraint("ck_outbox_message_payload", "jsonb_typeof(payload) = 'object'");
                t.HasCheckConstraint("ck_outbox_message_attempts", "attempts >= 0");
            });
            e.HasKey(x => x.Id).HasName("pk_outbox_message");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever().HasComment("Event id; receivers deduplicate by it.");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasComment("Time the event occurred.");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(300).HasComment("CLR type of the event (Namespace.Type, Assembly).");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").HasComment("Event serialized as a JSON object.");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasComment("Time the event was delivered to every consumer; NULL = pending.");
            e.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0).HasComment("Failed delivery attempts so far.");
            e.Property(x => x.Error).HasColumnName("error").HasMaxLength(MaxErrorLength).HasComment("Last delivery error, if any.");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at")
                .HasComment("Earliest time of the next delivery attempt after a failure (exponential back-off); NULL = now.");
            e.Property(x => x.ExhaustedAt).HasColumnName("exhausted_at")
                .HasComment("Time the event ran out of delivery attempts and was parked for an operator; NULL = still being delivered.");
            // The dispatcher claims the oldest deliverable events with FOR UPDATE SKIP LOCKED; parked ones stay out of the index.
            e.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_outbox_message_pending").HasFilter("processed_at IS NULL AND exhausted_at IS NULL");
            // Parked events, for the health check, the metric and mart.v_data_quality.
            e.HasIndex(x => x.ExhaustedAt).HasDatabaseName("ix_outbox_message_exhausted").HasFilter("exhausted_at IS NOT NULL AND processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_message", schema, t =>
                t.HasComment("Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003)."));
            e.HasKey(x => new { x.MessageId, x.Consumer }).HasName("pk_inbox_message");
            e.Property(x => x.MessageId).HasColumnName("message_id").HasComment("Id of the handled event.");
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(300).HasComment("Handler that consumed the event.");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasDefaultValueSql("now()").HasComment("Time the event was handled.");
        });

        return modelBuilder;
    }
}

/// <summary>Serializes integration events for the outbox and back.</summary>
public static class IntegrationEventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Type name stored in the outbox: <c>Namespace.Type, Assembly</c> (no version, survives upgrades).</summary>
    public static string TypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"{type.FullName}, {type.Assembly.GetName().Name}";
    }

    public static OutboxMessage ToOutboxMessage(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var type = integrationEvent.GetType();
        return new OutboxMessage
        {
            Id = integrationEvent.Id,
            OccurredAt = integrationEvent.OccurredAt,
            Type = TypeName(type),
            Payload = JsonSerializer.Serialize(integrationEvent, type, Options),
        };
    }

    public static IIntegrationEvent Deserialize(string typeName, string payload)
    {
        var type = Type.GetType(typeName, throwOnError: false)
            ?? throw new InvalidOperationException($"Integration event type '{typeName}' is not loaded.");
        if (!typeof(IIntegrationEvent).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"'{typeName}' is not an integration event.");
        }

        return (IIntegrationEvent)(JsonSerializer.Deserialize(payload, type, Options)
            ?? throw new InvalidOperationException($"Payload of '{typeName}' is empty."));
    }
}
