using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using SmartSpare.Infrastructure.Jobs;

namespace SmartSpare.Infrastructure.RabbitMq.IntegrationTests;

/// <summary>
/// The broker metrics job (STAGE_4_PLAN 6.2) against a real broker's management API: queue depths, dead letters,
/// connections and published messages become the gauges the dashboards and alert rules of ADR 0018 §4 read.
/// </summary>
[Trait("Category", "Integration")]
[Collection(BrokerGroup.Name)]
public sealed class BrokerMetricsJobTests(BrokerFixture broker)
{
    [Fact]
    public async Task The_job_reads_the_broker_and_publishes_its_gauges()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (queue, deadLetter) = ($"metrics-{suffix}.consumer", $"metrics-{suffix}.dead-letter");
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.ConnectionString) }.CreateConnectionAsync(Ct);
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
            await channel.QueueDeclareAsync(deadLetter, durable: true, exclusive: false, autoDelete: false, cancellationToken: Ct);
            for (var i = 0; i < 3; i++)
            {
                await channel.BasicPublishAsync(string.Empty, queue, Encoding.UTF8.GetBytes("{}"), Ct);
            }

            await channel.BasicPublishAsync(string.Empty, deadLetter, Encoding.UTF8.GetBytes("{}"), Ct);
        }

        await using var services = Services();
        using var listener = new GaugeListener();

        // The management API refreshes queue statistics every few seconds.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        BrokerSnapshot? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            (await JobRunner.RunAsync(services, BrokerMetricsJob.JobName, Ct)).ShouldBe(JobRunner.Succeeded);
            snapshot = services.GetRequiredService<BrokerMetrics>().Snapshot;
            if (snapshot!.Queues.Any(q => q.Queue == queue && q.Messages == 3) && snapshot.Queues.Any(q => q.Queue == deadLetter && q.Messages == 1))
            {
                break;
            }

            await Task.Delay(1000, Ct);
        }

        snapshot!.Connections.ShouldBeGreaterThanOrEqualTo(1, "this test's own connection");
        snapshot.PublishedTotal.ShouldNotBeNull();
        snapshot.PublishedTotal.Value.ShouldBeGreaterThanOrEqualTo(4);

        var gauges = listener.Collect();
        gauges[("smartspare.broker.queue.messages", queue)].ShouldBe(3);
        gauges[("smartspare.broker.dead_letter.messages", deadLetter)].ShouldBe(1);
        gauges.Keys.ShouldNotContain(("smartspare.broker.queue.messages", deadLetter), "dead letters are their own gauge");
        gauges[("smartspare.broker.connections", null)].ShouldBeGreaterThanOrEqualTo(1);
        gauges[("smartspare.broker.published.messages", null)].ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_refused_management_call_fails_the_job()
    {
        await using var services = Services(password: "wrong");

        (await JobRunner.RunAsync(services, BrokerMetricsJob.JobName, Ct)).ShouldBe(JobRunner.Failed);
    }

    private ServiceProvider Services(string? password = null)
    {
        var url = new UriBuilder(broker.ConnectionString);
        if (password is not null)
        {
            url.Password = password;
        }

        var services = new ServiceCollection().AddLogging().AddMetrics();
        services.AddOptions<RabbitMqOptions>().Configure(o =>
        {
            o.ConnectionString = url.Uri.ToString();
            o.ManagementUrl = broker.ManagementUrl;
        });
        services.AddBrokerMetricsJob();
        return services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reads the broker gauges as an exporter would: (instrument, queue) → value.</summary>
    private sealed class GaugeListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<(string, string?), long> _values = [];

        public GaugeListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name.StartsWith("smartspare.broker.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? queue = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "queue")
                    {
                        queue = tag.Value as string;
                    }
                }

                _values[(instrument.Name, queue)] = value;
            });
            _listener.Start();
        }

        public Dictionary<(string Instrument, string? Queue), long> Collect()
        {
            _values.Clear();
            _listener.RecordObservableInstruments();
            return new(_values);
        }

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>The management client's settings from the AMQP URL; no broker needed.</summary>
public sealed class BrokerManagementClientTests
{
    [Theory]
    [InlineData("amqps://user:secret@lemur.rmq.cloudamqp.com/abcdefgh", "abcdefgh")]
    [InlineData("amqp://guest:guest@localhost:5672/", "/")]
    [InlineData("amqp://guest:guest@localhost:5672", "/")]
    [InlineData("amqp://guest:guest@localhost/%2Fstaging", "/staging")]
    public void The_vhost_is_the_path_of_the_url_or_the_default(string url, string vhost) =>
        BrokerManagementClient.VhostOf(url).ShouldBe(vhost);

    [Fact]
    public void CloudAMQP_serves_the_api_over_https_on_the_instance_host_with_the_amqp_credentials()
    {
        using var http = new HttpClient();

        BrokerManagementClient.Configure(http, new RabbitMqOptions { ConnectionString = "amqps://us%40r:p%3Ass@lemur.rmq.cloudamqp.com/vhost" });

        http.BaseAddress.ShouldBe(new Uri("https://lemur.rmq.cloudamqp.com/"));
        http.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(http.DefaultRequestHeaders.Authorization.Parameter!)).ShouldBe("us@r:p:ss");
    }

    [Fact]
    public void A_management_url_overrides_the_host()
    {
        using var http = new HttpClient();

        BrokerManagementClient.Configure(http, new RabbitMqOptions { ConnectionString = "amqp://guest:guest@localhost:5672/", ManagementUrl = "http://localhost:15672" });

        http.BaseAddress.ShouldBe(new Uri("http://localhost:15672/"));
    }

    [Fact]
    public async Task Only_the_urls_vhost_counts_and_queues_without_statistics_yet_count_as_empty()
    {
        using var http = new HttpClient(new StubApi()) { BaseAddress = new Uri("http://broker/") };
        var client = new BrokerManagementClient(http, Options.Create(new RabbitMqOptions { ConnectionString = "amqp://guest:guest@broker/" }));

        var snapshot = await client.ReadAsync(TestContext.Current.CancellationToken);

        snapshot.Queues.ShouldBe([new QueueDepth("inventory.dead-letter", 2), new QueueDepth("new.queue", 0)]);
        snapshot.Connections.ShouldBe(1);
        snapshot.PublishedTotal.ShouldBeNull("a vhost without traffic has no message_stats");
    }

    private sealed class StubApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/queues" => """[{"name":"new.queue","vhost":"/"},{"name":"inventory.dead-letter","vhost":"/","messages":2},{"name":"x","vhost":"other","messages":9}]""",
                "/api/connections" => """[{"name":"c1","vhost":"/"},{"name":"c2","vhost":"other"}]""",
                "/api/vhosts" => """[{"name":"/","message_stats":[]},{"name":"other","message_stats":{"publish":5}}]""",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
