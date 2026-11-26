using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SmartSpare.AppHost;

/// <summary>
/// <c>--topology services</c> (ADR 0015, STAGE_4_PLAN 4g, 6.3): the gateway and the six module services instead of the
/// single host, RabbitMQ (or CloudAMQP, when the AppHost has <c>ConnectionStrings:messaging</c>), and the observability
/// stack: an OpenTelemetry Collector forwarding to Prometheus, Loki, Tempo and the Aspire dashboard, and Grafana with the
/// dashboards and alert rules of <c>infra/observability/</c>. With the <c>GrafanaCloud:*</c> user secrets, the collector
/// also forwards everything to the Grafana Cloud stack (STAGE_4_PLAN 4j). The broker metrics job is a resource that
/// runs once when started from the dashboard.
/// </summary>
internal static class ServicesTopology
{
    public const string ArgumentValue = "services";

    private const string GatewayProject = "../Bootstrapper/SmartSpare.Gateway/SmartSpare.Gateway.csproj";
    private const string Observability = "../../infra/observability";

    /// <summary>The job of the Reporting image that reads the broker (STAGE_4_PLAN 6.2); BrokerMetricsJob.JobName.</summary>
    private const string BrokerMetricsJob = "reporting.broker-metrics";

    /// <summary>The services in catalogue order; the resource name is the host name the gateway calls (<c>http://identity</c>).</summary>
    private static readonly string[] Modules = ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    public static bool IsRequested(IConfiguration configuration) =>
        string.Equals(configuration["topology"], ArgumentValue, StringComparison.OrdinalIgnoreCase);

    public static void Add(
        IDistributedApplicationBuilder builder,
        EndpointReference database,
        IReadOnlyDictionary<string, (string Login, IResourceBuilder<ParameterResource> Password)> logins,
        IResourceBuilder<Aspire.Hosting.Dotnet.DotnetProjectResource> migrations)
    {
        AddObservability(builder);

        // CloudAMQP by configuration (STAGE_4_PLAN 5.5): `dotnet user-secrets set ConnectionStrings:messaging "amqps://..."
        // --project src/SmartSpare.AppHost`. Otherwise a local RabbitMQ with the management UI.
        IResourceBuilder<IResourceWithConnectionString> messaging;
        EndpointReference? management = null;
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("messaging")))
        {
            var rabbit = builder.AddRabbitMQ("messaging").WithManagementPlugin().WithImageTag("4.4-management").WithDataVolume("smartspare-rabbitmq-data");
            management = rabbit.GetEndpoint("management");
            messaging = rabbit;
        }
        else
        {
            // CloudAMQP serves the management API over HTTPS on the instance host, which the job derives from the URL.
            messaging = builder.AddConnectionString("messaging");
        }

        // A signing key per AppHost run: internal tokens live 2 minutes and sessions are cookies, so nothing outlives it.
        // The gateway gets the private key, the services only the public JWKS (ADR 0016 section 2).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingKey = builder.AddParameter("internal-token-signing-key", key.ExportPkcs8PrivateKeyPem(), secret: true);
        var jwks = PublicJwks(key);

        var services = new List<IResourceBuilder<Aspire.Hosting.Dotnet.DotnetProjectResource>>();
        foreach (var module in Modules)
        {
            var (login, password) = logins[module];
            var service = builder.AddDotnetProject(module, ServiceProject(module))
                .WithHttpEndpoint()
                .WithEnvironment("ConnectionStrings__smartspare", ReferenceExpression.Create(
                    $"Host={database.Property(EndpointProperty.Host)};Port={database.Property(EndpointProperty.Port)};Database=smartspare;Username={login};Password={password}"))
                .WithReference(messaging)
                .WithEnvironment("InternalTokens__Jwks", jwks)
                .WaitForCompletion(migrations)
                .WaitFor(messaging)
                .WithHttpHealthCheck("/health/ready");
            services.Add(service);
        }

        // The broker metrics job on demand (STAGE_4_PLAN 4j, 6.2): the Reporting image with --job, started from the
        // dashboard; it reads the broker once, exports its gauges through the collector and exits. Its cron schedule in
        // Azure comes with the first deployment (#85).
        var (reportingLogin, reportingPassword) = logins["reporting"];
        var brokerMetrics = builder.AddDotnetProject("broker-metrics", ServiceProject("reporting"))
            .WithArgs("--job", BrokerMetricsJob)
            .WithEnvironment("ConnectionStrings__smartspare", ReferenceExpression.Create(
                $"Host={database.Property(EndpointProperty.Host)};Port={database.Property(EndpointProperty.Port)};Database=smartspare;Username={reportingLogin};Password={reportingPassword}"))
            .WithReference(messaging)
            .WithEnvironment("InternalTokens__Jwks", jwks)
            .WaitFor(messaging)
            .WithExplicitStart();
        if (management is not null)
        {
            brokerMetrics.WithEnvironment("RabbitMq__ManagementUrl", management);
        }

        var gateway = builder.AddDotnetProject("gateway", GatewayProject)
            .WithEnvironment("InternalTokens__SigningKey", signingKey)
            .WithEnvironment("DataProtection__KeysDirectory", KeysDirectory())
            .WithHttpHealthCheck("/health/ready");
        foreach (var service in services)
        {
            gateway.WithReference(service).WaitFor(service);
        }
    }

    private static void AddObservability(IDistributedApplicationBuilder builder)
    {
        // Every project's OTLP goes to the collector, which forwards to the stack and to the Aspire dashboard.
        var collector = builder.AddOpenTelemetryCollector("otelcollector", settings => settings.CollectorTag = "0.166.0")
            .WithConfig($"{Observability}/collector/config.yaml")
            .WithAppForwarding();

        // And to Grafana Cloud, when the AppHost has the stack's OTLP endpoint and an access policy token with
        // metrics:write, logs:write and traces:write (infra/grafana/README.md).
        if (GrafanaCloudOtlp(builder.Configuration) is { } cloud)
        {
            var authorization = builder.AddParameter("grafana-cloud-otlp-authorization", cloud.Authorization, secret: true);
            collector.WithConfig($"{Observability}/collector/grafana-cloud.yaml")
                .WithEnvironment("GRAFANA_CLOUD_OTLP_ENDPOINT", cloud.Endpoint)
                .WithEnvironment("GRAFANA_CLOUD_OTLP_AUTHORIZATION", authorization);
        }

        builder.AddContainer("prometheus", "prom/prometheus", "v3.16.0")
            .WithBindMount($"{Observability}/prometheus", "/etc/prometheus", isReadOnly: true)
            .WithVolume("smartspare-prometheus-data", "/prometheus")
            .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.path=/prometheus",
                "--web.enable-otlp-receiver", "--web.enable-remote-write-receiver")
            .WithHttpEndpoint(targetPort: 9090, name: "http");

        builder.AddContainer("loki", "grafana/loki", "3.7.10")
            .WithBindMount($"{Observability}/loki/config.yaml", "/etc/loki/config.yaml", isReadOnly: true)
            .WithVolume("smartspare-loki-data", "/loki")
            .WithArgs("-config.file=/etc/loki/config.yaml")
            .WithHttpEndpoint(targetPort: 3100, name: "http");

        builder.AddContainer("tempo", "grafana/tempo", "3.1.1")
            .WithBindMount($"{Observability}/tempo/tempo.yaml", "/etc/tempo/tempo.yaml", isReadOnly: true)
            .WithVolume("smartspare-tempo-data", "/var/tempo")
            .WithArgs("-config.file=/etc/tempo/tempo.yaml")
            .WithHttpEndpoint(targetPort: 3200, name: "http");

        // Anonymous viewers locally; the admin password is generated once and kept in the AppHost user secrets.
        var grafanaPassword = builder.AddParameter("grafana-admin-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
        builder.AddContainer("grafana", "grafana/grafana", "13.3.2")
            .WithBindMount($"{Observability}/grafana/provisioning", "/etc/grafana/provisioning", isReadOnly: true)
            .WithBindMount($"{Observability}/dashboards", "/var/lib/grafana/dashboards", isReadOnly: true)
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Viewer")
            .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", grafanaPassword)
            .WithEnvironment("GF_ANALYTICS_REPORTING_ENABLED", "false")
            .WithHttpEndpoint(targetPort: 3000, name: "http");
    }

    /// <summary>
    /// The stack's OTLP endpoint and the Basic authorization header (instance id and token), or null without the
    /// <c>GrafanaCloud:OtlpEndpoint</c>, <c>GrafanaCloud:InstanceId</c> and <c>GrafanaCloud:Token</c> user secrets.
    /// </summary>
    private static (string Endpoint, string Authorization)? GrafanaCloudOtlp(ConfigurationManager configuration)
    {
        var endpoint = configuration["GrafanaCloud:OtlpEndpoint"];
        var instance = configuration["GrafanaCloud:InstanceId"];
        var token = configuration["GrafanaCloud:Token"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("GrafanaCloud:OtlpEndpoint must be the stack's https://otlp-gateway-<region>.grafana.net/otlp URL.");
        }

        return (endpoint.TrimEnd('/'), "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{instance.Trim()}:{token.Trim()}")));
    }

    private static string ServiceProject(string module)
    {
        var name = char.ToUpperInvariant(module[0]) + module[1..];
        return $"../Bootstrapper/Services/SmartSpare.Services.{name}/SmartSpare.Services.{name}.csproj";
    }

    /// <summary>The gateway's key ring outlives restarts, so signed-in sessions survive them (ADR 0016 section 6).</summary>
    private static string KeysDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartSpare", "gateway-keys");

    /// <summary>The public JWKS of <paramref name="key"/> with the RFC 7638 thumbprint as <c>kid</c>, as the gateway signs.</summary>
    private static string PublicJwks(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var x = Base64Url(parameters.Q.X!);
        var y = Base64Url(parameters.Q.Y!);
        var kid = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
        return JsonSerializer.Serialize(new { keys = new[] { new { kty = "EC", crv = "P-256", x, y, kid, use = "sig", alg = "ES256" } } });
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
