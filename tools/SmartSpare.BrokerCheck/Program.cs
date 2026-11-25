// Checks a RabbitMQ broker for what the SmartSpare transport relies on (STAGE_4_PLAN 5.4, ADR 0017 §5):
//   dotnet user-secrets set ConnectionStrings:messaging "amqps://user:password@host/vhost" --id smartspare-brokercheck
//   dotnet run --project tools/SmartSpare.BrokerCheck
// or set ConnectionStrings__messaging in the environment. Prints a Markdown table; never prints the credentials. Every
// queue, exchange and policy it creates is named smartspare-check.* and deleted at the end.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Check).Assembly)
    .AddEnvironmentVariables()
    .Build();
var url = configuration.GetConnectionString("messaging");
if (!Uri.TryCreate(url, UriKind.Absolute, out var amqp) || amqp.Scheme is not ("amqp" or "amqps"))
{
    Console.Error.WriteLine("Set ConnectionStrings:messaging to the broker's amqp:// or amqps:// URL (user-secrets id smartspare-brokercheck).");
    return 2;
}

var vhost = Uri.UnescapeDataString(amqp.AbsolutePath.TrimStart('/'));
vhost = vhost.Length == 0 ? "/" : vhost;
var userInfo = amqp.UserInfo.Split(':', 2);
var (user, password) = (Uri.UnescapeDataString(userInfo[0]), userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty);
var results = new List<Check>();
Console.WriteLine($"Broker check of {amqp.Scheme}://{amqp.Host}:{(amqp.IsDefaultPort ? (amqp.Scheme == "amqps" ? 5671 : 5672) : amqp.Port)} (vhost '{vhost}'), {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");

await using var connection = await new ConnectionFactory { Uri = amqp, ClientProvidedName = "smartspare-brokercheck" }.CreateConnectionAsync();
results.Add(new("AMQP(S) connection", true, "connected"));
var version = connection.ServerProperties?.TryGetValue("version", out var v) == true ? Encoding.UTF8.GetString((byte[])v!) : "unknown";
results.Add(new("RabbitMQ version (server properties)", true, version));

await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
const string Exchange = "smartspare-check.events";
const string Dlx = "smartspare-check.dlx";
const string Quorum = "smartspare-check.quorum";
const string Delayed = "smartspare-check.delayed-retry";
await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true);
await channel.ExchangeDeclareAsync(Dlx, ExchangeType.Topic, durable: true);

results.Add(await Try("Quorum queue with delivery limit and at-least-once dead-lettering (queue arguments)", async () =>
{
    await channel.QueueDeclareAsync(Quorum, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-delivery-limit"] = 10,
        ["x-dead-letter-exchange"] = Dlx,
        ["x-dead-letter-strategy"] = "at-least-once",
        ["x-overflow"] = "reject-publish",
    });
    await channel.QueueBindAsync(Quorum, Exchange, "Probe.v1");
    return "declared";
}));

results.Add(await Try("Confirmed publish (mandatory) and consume on the quorum queue", async () =>
{
    await channel.BasicPublishAsync(Exchange, "Probe.v1", mandatory: true, new BasicProperties { DeliveryMode = DeliveryModes.Persistent }, "{}"u8.ToArray());
    var got = await channel.BasicGetAsync(Quorum, autoAck: true);
    return got is null ? throw new InvalidOperationException("published and confirmed, but nothing to consume") : "confirmed and consumed";
}));

// Delayed retry needs RabbitMQ 4.3+; each attempt opens its own channel, because a failed declaration closes the channel.
results.Add(await Try("Native delayed retry (x-delayed-retry-*, RabbitMQ 4.3+)", async () =>
{
    await using var probe = await connection.CreateChannelAsync();
    await probe.QueueDeclareAsync(Delayed, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
    {
        ["x-queue-type"] = "quorum",
        ["x-delayed-retry-type"] = "failed",
        ["x-delayed-retry-min"] = 5000,
        ["x-delayed-retry-max"] = 120000,
    });
    return "declared (whether the broker applies it is visible in its management UI)";
}));

// Management HTTP API: what KEDA's rabbitmq scaler calls in http mode, and what policies need.
// CloudAMQP serves it over HTTPS on the instance host; Management:Url overrides it (e.g. http://localhost:15672/ locally).
using var http = new HttpClient { BaseAddress = new Uri(configuration["Management:Url"] ?? $"https://{amqp.Host}/"), Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
var vhostPath = Uri.EscapeDataString(vhost);

results.Add(await Try("Management API GET /api/overview", async () =>
{
    using var doc = JsonDocument.Parse(await Get("api/overview"));
    return $"RabbitMQ {Text(doc, "rabbitmq_version")}, management {Text(doc, "management_version")}";
}));
results.Add(await Try($"Management API GET /api/queues/{vhostPath}/{Quorum} (KEDA http mode)", async () =>
{
    using var doc = JsonDocument.Parse(await Get($"api/queues/{vhostPath}/{Quorum}"));
    return $"type {Text(doc, "type")}, messages {Text(doc, "messages")}";
}));
results.Add(await Try("User policy (PUT and DELETE /api/policies)", async () =>
{
    var body = new StringContent("""{"pattern":"^smartspare-check\\.","definition":{"delivery-limit":5},"apply-to":"queues","priority":0}""", Encoding.UTF8, "application/json");
    using var put = await http.PutAsync($"api/policies/{vhostPath}/smartspare-check", body);
    if (!put.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"PUT answered {(int)put.StatusCode} {put.ReasonPhrase}");
    }

    using var delete = await http.DeleteAsync($"api/policies/{vhostPath}/smartspare-check");
    return $"created ({(int)put.StatusCode}) and deleted ({(int)delete.StatusCode})";
}));

// Clean up everything the check created.
await using (var cleanup = await connection.CreateChannelAsync())
{
    foreach (var queue in new[] { Quorum, Delayed })
    {
        try { await cleanup.QueueDeleteAsync(queue); } catch (Exception) { /* not created */ }
    }

    await cleanup.ExchangeDeleteAsync(Exchange);
    await cleanup.ExchangeDeleteAsync(Dlx);
}

Console.WriteLine();
Console.WriteLine("| Check | Result | Detail |");
Console.WriteLine("|---|---|---|");
foreach (var r in results)
{
    Console.WriteLine($"| {r.Name} | {(r.Ok ? "yes" : "**no**")} | {r.Detail.Replace("|", "/", StringComparison.Ordinal)} |");
}

return results.All(r => r.Ok) ? 0 : 1;

async Task<string> Get(string path)
{
    using var response = await http.GetAsync(path);
    return response.IsSuccessStatusCode
        ? await response.Content.ReadAsStringAsync()
        : throw new InvalidOperationException($"GET {path} answered {(int)response.StatusCode} {response.ReasonPhrase}");
}

static string Text(JsonDocument doc, string property) =>
    doc.RootElement.TryGetProperty(property, out var value) ? value.ToString() : "?";

static async Task<Check> Try(string name, Func<Task<string>> check)
{
    try
    {
        return new Check(name, true, await check());
    }
#pragma warning disable CA1031 // A failed check is a result, not a crash.
    catch (Exception ex)
#pragma warning restore CA1031
    {
        return new Check(name, false, ex.GetBaseException().Message);
    }
}

internal sealed record Check(string Name, bool Ok, string Detail);
