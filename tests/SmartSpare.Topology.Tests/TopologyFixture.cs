extern alias gateway;
extern alias runner;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity;
using SmartSpare.Services.Identity;
using SmartSpare.Services.Inventory;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Yarp.ReverseProxy.Forwarder;
using runner::SmartSpare.MigrationRunner;
using GatewayClients = gateway::SmartSpare.Gateway.GatewayClients;
using GatewayProgram = gateway::SmartSpare.Gateway.GatewayProgram;

namespace SmartSpare.Topology.Tests;

/// <summary>
/// The services topology of ADR 0015 in one process: PostgreSQL 17 built by the migration runner (with the service
/// logins), RabbitMQ 4.4, the Identity and Inventory service hosts on their own logins and the broker, and the gateway
/// in front of them. The gateway reaches each service by host name through an in-memory handler that propagates the
/// W3C trace context, as the platform's HTTP stack does. Every activity of the process is recorded.
/// </summary>
public sealed class TopologyFixture : IAsyncLifetime
{
    public const string Password = "a long enough demo password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.4").Build();
    private readonly ActivityListener _listener;
    private string _migrator = null!;

    public TopologyFixture() =>
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Activities.Add,
        };

    public ConcurrentBag<Activity> Activities { get; } = [];

    /// <summary>Every request a service host received, by service, as the gateway forwarded it.</summary>
    public ConcurrentQueue<(string Service, string Path)> Forwarded { get; } = new();

    public ServiceFactory<IdentityService> Identity { get; private set; } = null!;

    public ServiceFactory<InventoryService> Inventory { get; private set; } = null!;

    public GatewayFactory Gateway { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        ActivitySource.AddActivityListener(_listener);
        await Task.WhenAll(_postgres.StartAsync(Ct), _rabbit.StartAsync(Ct));

        var passwords = new BootstrapPasswords
        {
            Migrator = NewPassword(),
            Api = NewPassword(),
            Jobs = NewPassword(),
            Reporting = NewPassword(),
            Identity = NewPassword(),
            Inventory = NewPassword(),
        };
        var options = new MigrationRunnerOptions { AdminConnectionString = _postgres.GetConnectionString(), Passwords = passwords };
        await new DatabaseMigrator(options, SmartSpare.ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(Ct);
        _migrator = options.BuildMigratorConnectionString();

        // Each service on its own login (ADR 0015 §5): the tests also prove those logins' privileges are enough.
        Identity = new ServiceFactory<IdentityService>(Login("smartspare_identity", passwords.Identity!), _rabbit.GetConnectionString());
        Inventory = new ServiceFactory<InventoryService>(Login("smartspare_inventory", passwords.Inventory!), _rabbit.GetConnectionString());
        Gateway = new GatewayFactory(this);

        // Start both services, so Inventory's queue exists before anything is published.
        _ = Identity.Server;
        _ = Inventory.Server;
    }

    public async ValueTask DisposeAsync()
    {
        await Gateway.DisposeAsync();
        await Inventory.DisposeAsync();
        await Identity.DisposeAsync();
        _listener.Dispose();
        await _rabbit.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public async Task<Guid> CreateUserAsync(string userName, string[] roles)
    {
        var id = Guid.NewGuid();
        var hasherOptions = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(hasherOptions);
        var hash = new PasswordHasher<object>(Options.Create(hasherOptions)).HashPassword(new object(), Password);
        await using var connection = new NpgsqlConnection(_migrator);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO iam.app_user (id, user_name, email, full_name, password_hash) VALUES (@id, @name, @name || '@dealer.example', 'Topology User', @hash);
            INSERT INTO iam.user_role (user_id, role_id) SELECT @id, r.id FROM iam.role r WHERE r.code = ANY(@roles);
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("roles", roles);
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_migrator);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, parameter) in parameters)
        {
            command.Parameters.AddWithValue(name, parameter);
        }

        return await command.ExecuteScalarAsync(Ct) is T value ? value : default;
    }

    internal static string SigningKeyPem { get; } = NewSigningKey();

    internal static string PublicJwks()
    {
        using var keys = new InternalTokenKeyRing(Options.Create(new InternalTokenOptions { SigningKey = SigningKeyPem }));
        return keys.PublicJwks();
    }

    private string Login(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "smartspare", Username = user, Password = password }.ConnectionString;

    private static string NewSigningKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A service host with only the public JWKS, its own login and the real broker.</summary>
    public sealed class ServiceFactory<TEntryPoint>(string connectionString, string broker) : WebApplicationFactory<TEntryPoint>
        where TEntryPoint : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.Jwks)}", PublicJwks());
            builder.UseSetting("ConnectionStrings:smartspare", connectionString);
            builder.UseSetting("ConnectionStrings:messaging", broker);
        }
    }

    /// <summary>The gateway; <c>identity</c> and <c>inventory</c> are the real service hosts, every other service answers 503.</summary>
    public sealed class GatewayFactory(TopologyFixture topology) : WebApplicationFactory<GatewayProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.SigningKey)}", SigningKeyPem);
            builder.ConfigureTestServices(services =>
            {
                var services_ = new ServiceHandler(topology);
                services.AddSingleton<IForwarderHttpClientFactory>(new Forwarder(services_));
                services.AddHttpClient(GatewayClients.Identity).ConfigurePrimaryHttpMessageHandler(() => services_);
            });
        }

        private sealed class Forwarder(HttpMessageHandler handler) : IForwarderHttpClientFactory
        {
            public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handler, disposeHandler: false);
        }
    }

    /// <summary>Routes by host name to the in-memory service hosts and injects <c>traceparent</c>, as SocketsHttpHandler does.</summary>
    private sealed class ServiceHandler(TopologyFixture topology) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var service = request.RequestUri!.Host;
            topology.Forwarded.Enqueue((service, request.RequestUri.AbsolutePath));
            if (Activity.Current is { } activity)
            {
                request.Headers.Remove("traceparent");
                DistributedContextPropagator.Current.Inject(activity, request, static (carrier, name, header) =>
                    ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(name, header));
            }

            HttpMessageHandler? target = service switch
            {
                "identity" => topology.Identity.Server.CreateHandler(),
                "inventory" => topology.Inventory.Server.CreateHandler(),
                _ => null,
            };
            return target is null
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : new HttpMessageInvoker(target).SendAsync(request, cancellationToken);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class TopologyGroup : ICollectionFixture<TopologyFixture>
{
    public const string Name = "Services topology";
}
