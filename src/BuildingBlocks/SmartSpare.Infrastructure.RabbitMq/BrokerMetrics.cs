using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Infrastructure.RabbitMq;

/// <summary>What the broker reports for our vhost at one moment.</summary>
/// <param name="Queues">Every queue of the vhost with its ready and unacknowledged messages.</param>
/// <param name="Connections">Open AMQP connections to the vhost (the free plan allows 20).</param>
/// <param name="PublishedTotal">
/// Messages published to the vhost since the broker last reset its statistics; null when the broker reports none yet.
/// </param>
public sealed record BrokerSnapshot(IReadOnlyList<QueueDepth> Queues, int Connections, long? PublishedTotal);

/// <summary>Messages in one queue; <see cref="IsDeadLetter"/> for the <c>&lt;module&gt;.dead-letter</c> queues.</summary>
public sealed record QueueDepth(string Queue, long Messages)
{
    public bool IsDeadLetter => Queue.EndsWith(".dead-letter", StringComparison.Ordinal);
}

/// <summary>
/// Reads the RabbitMQ management HTTP API (STAGE_4_PLAN 6.2) with the credentials and vhost of the AMQP URL, the
/// endpoints KEDA's http mode uses too (BROKER.md). Opens no AMQP connection, so it costs nothing of the connection budget.
/// </summary>
public sealed class BrokerManagementClient(HttpClient http, IOptions<RabbitMqOptions> options)
{
    public const string HttpClientName = "rabbitmq-management";

    private readonly string _vhost = VhostOf(options.Value.ConnectionString);

    public async Task<BrokerSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        // The list endpoints, filtered by vhost here: an escaped "/" vhost (%2F) in a path doesn't survive every HTTP stack,
        // and a CloudAMQP user sees only its own vhost anyway.
        using var queues = await GetAsync("api/queues?columns=name,vhost,messages", cancellationToken).ConfigureAwait(false);
        using var connections = await GetAsync("api/connections?columns=name,vhost", cancellationToken).ConfigureAwait(false);
        using var vhosts = await GetAsync("api/vhosts?columns=name,message_stats.publish", cancellationToken).ConfigureAwait(false);

        var depths = InVhost(queues)
            .Select(q => new QueueDepth(
                q.GetProperty("name").GetString()!,
                // A queue declared moments ago has no statistics yet.
                q.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Number ? messages.GetInt64() : 0))
            .OrderBy(q => q.Queue, StringComparer.Ordinal)
            .ToList();

        // Without traffic yet, RabbitMQ answers "message_stats": [] instead of an object.
        long? published = InVhost(vhosts, "name").FirstOrDefault() is { ValueKind: JsonValueKind.Object } vhost
                          && vhost.TryGetProperty("message_stats", out var stats) && stats.ValueKind == JsonValueKind.Object
                          && stats.TryGetProperty("publish", out var publish) && publish.ValueKind == JsonValueKind.Number
            ? publish.GetInt64()
            : null;
        return new BrokerSnapshot(depths, InVhost(connections).Count(), published);
    }

    private IEnumerable<JsonElement> InVhost(JsonDocument list, string property = "vhost") =>
        list.RootElement.EnumerateArray().Where(e => e.TryGetProperty(property, out var v) && v.GetString() == _vhost);

    /// <summary>The management API's base address and Basic credentials, from the AMQP URL (or <see cref="RabbitMqOptions.ManagementUrl"/>).</summary>
    public static void Configure(HttpClient http, RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        var amqp = new Uri(options.ConnectionString ?? throw new InvalidOperationException("No AMQP URL in ConnectionStrings:messaging."));
        http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(options.ManagementUrl) ? $"https://{amqp.Host}/" : options.ManagementUrl.TrimEnd('/') + "/");
        var credentials = amqp.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(credentials[0]);
        var password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : string.Empty;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        http.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>The vhost of an AMQP URL: its path, or <c>/</c> when it has none (on CloudAMQP the vhost is the user name).</summary>
    public static string VhostOf(string? amqpUrl)
    {
        var vhost = Uri.UnescapeDataString(new Uri(amqpUrl ?? "amqp://localhost/").AbsolutePath.TrimStart('/'));
        return vhost.Length == 0 ? "/" : vhost;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The broker's management API answered {(int)response.StatusCode} {response.ReasonPhrase} for {path.Split('?')[0]}.",
                null,
                response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The broker's gauges on the messaging meter (ADR 0018 §4, <c>infra/observability</c>): queued and dead-lettered
/// messages per queue, connections, and published messages. Prometheus names them
/// <c>smartspare_broker_queue_messages</c>, <c>smartspare_broker_dead_letter_messages</c>,
/// <c>smartspare_broker_connections</c> and <c>smartspare_broker_published_messages</c>.
/// </summary>
public sealed class BrokerMetrics : IDisposable
{
    private readonly Meter _meter;
    private volatile BrokerSnapshot? _snapshot;

    public BrokerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(OutboxMonitor.MeterName);
        _meter.CreateObservableGauge(
            "smartspare.broker.queue.messages",
            () => Queues(dead: false),
            unit: "{message}",
            description: "Messages in a consumer queue, ready or unacknowledged.");
        _meter.CreateObservableGauge(
            "smartspare.broker.dead_letter.messages",
            () => Queues(dead: true),
            unit: "{message}",
            description: "Messages in a module's dead-letter queue: they ran out of deliveries and wait for an operator.");
        _meter.CreateObservableGauge<long>(
            "smartspare.broker.connections",
            () => _snapshot is { } s ? [new Measurement<long>(s.Connections)] : [],
            unit: "{connection}",
            description: "Open AMQP connections to the vhost.");
        _meter.CreateObservableGauge<long>(
            "smartspare.broker.published.messages",
            () => _snapshot is { PublishedTotal: { } total } ? [new Measurement<long>(total)] : [],
            unit: "{message}",
            description: "Messages published to the vhost since the broker last reset its statistics.");
    }

    public BrokerSnapshot? Snapshot => _snapshot;

    public void Update(BrokerSnapshot snapshot) => _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> Queues(bool dead) =>
        _snapshot?.Queues.Where(q => q.IsDeadLetter == dead).Select(q => new Measurement<long>(q.Messages, new KeyValuePair<string, object?>("queue", q.Queue)))
        ?? [];
}

/// <summary>
/// <c>--job reporting.broker-metrics</c> (STAGE_4_PLAN 6.2): reads the broker once, publishes the gauges and exits. In
/// Azure a Container Apps cron job runs it every 15 minutes from the Reporting image (#85); the service host flushes the
/// telemetry before the process ends.
/// </summary>
public sealed partial class BrokerMetricsJob(BrokerManagementClient broker, BrokerMetrics metrics, ILogger<BrokerMetricsJob> logger) : IJob
{
    public const string JobName = "reporting.broker-metrics";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var snapshot = await broker.ReadAsync(cancellationToken).ConfigureAwait(false);
        metrics.Update(snapshot);
        var queued = snapshot.Queues.Where(q => !q.IsDeadLetter).Sum(q => q.Messages);
        var deadLettered = snapshot.Queues.Where(q => q.IsDeadLetter).Sum(q => q.Messages);
        LogSnapshot(snapshot.Queues.Count, queued, deadLettered, snapshot.Connections, snapshot.PublishedTotal);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Broker: {Queues} queues, {Queued} queued and {DeadLettered} dead-lettered messages, {Connections} connections, {Published} published since the last statistics reset.")]
    private partial void LogSnapshot(int queues, long queued, long deadLettered, int connections, long? published);
}

public static class BrokerMetricsRegistration
{
    /// <summary>Registers <see cref="BrokerMetricsJob"/>; the Reporting service calls it after <c>AddRabbitMqTransport</c>.</summary>
    public static IServiceCollection AddBrokerMetricsJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<BrokerManagementClient>(BrokerManagementClient.HttpClientName)
            .ConfigureHttpClient((sp, http) => BrokerManagementClient.Configure(http, sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value));
        services.AddSingleton<BrokerMetrics>();
        services.AddJob<BrokerMetricsJob>();
        return services;
    }
}
