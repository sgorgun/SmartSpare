using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SmartSpare.AppHost;

/// <summary>
/// <c>--topology services</c> (ADR 0015, STAGE_4_PLAN 4g, 6.3): the gateway and the six module services instead of the
/// single host, RabbitMQ (or CloudAMQP, when the AppHost has <c>ConnectionStrings:messaging</c>), and the observability
/// stack: an OpenTelemetry Collector forwarding to Prometheus, Loki, Tempo and the Aspire dashboard, and Grafana with the
/// dashboards and alert rules of <c>infra/observability/</c>.
/// </summary>
internal static class ServicesTopology
{
    public const string ArgumentValue = "services";

    private const string GatewayProject = "../Bootstrapper/SmartSpare.Gateway/SmartSpare.Gateway.csproj";
    private const string Observability = "../../infra/observability";

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
        IResourceBuilder<IResourceWithConnectionString> messaging = string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("messaging"))
            ? builder.AddRabbitMQ("messaging").WithManagementPlugin().WithImageTag("4.4-management").WithDataVolume("smartspare-rabbitmq-data")
            : builder.AddConnectionString("messaging");

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
        builder.AddOpenTelemetryCollector("otelcollector", settings => settings.CollectorTag = "0.166.0")
            .WithConfig($"{Observability}/collector/config.yaml")
            .WithAppForwarding();

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
