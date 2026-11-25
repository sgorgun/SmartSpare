namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>Settings of the RabbitMQ transport (section <c>RabbitMq</c>; the URL is the connection string <c>messaging</c>).</summary>
public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>Name of the connection string holding the AMQP(S) URL; the same name Aspire gives the broker resource.</summary>
    public const string ConnectionStringName = "messaging";

    /// <summary>AMQP or AMQPS URL, including the credentials and the vhost. From Key Vault or user-secrets, never a file.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Shown in the broker's connection list, for example <c>inventory</c>.</summary>
    public string ClientName { get; set; } = "smartspare";

    /// <summary>Unacknowledged messages per consumer channel (ADR 0017 §3).</summary>
    public ushort Prefetch { get; set; } = 10;

    /// <summary>How long a publish waits for the broker's confirm before it counts as a failed attempt.</summary>
    public TimeSpan ConfirmTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Deliveries of one message before the quorum queue dead-letters it (ADR 0017 §3).</summary>
    public int DeliveryLimit { get; set; } = 10;
}
