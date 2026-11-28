extern alias gateway;
extern alias runner;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Services.Fleet;
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

    public ServiceFactory<FleetService> Fleet { get; private set; } = null!;

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
            Fleet = NewPassword(),
        };
        var options = new MigrationRunnerOptions { AdminConnectionString = _postgres.GetConnectionString(), Passwords = passwords };
        await new DatabaseMigrator(options, SmartSpare.ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(Ct);
        _migrator = options.BuildMigratorConnectionString();

        // Each service on its own login (ADR 0015 §5): the tests also prove those logins' privileges are enough.
        Identity = new ServiceFactory<IdentityService>(Login("smartspare_identity", passwords.Identity!), _rabbit.GetConnectionString());
        // Inventory reads employees from its own copy, fed over RabbitMQ (ADR 0029 amendment 2): no call to Identity.
        Inventory = new ServiceFactory<InventoryService>(Login("smartspare_inventory", passwords.Inventory!), _rabbit.GetConnectionString());

        // Fleet reads hubs from its own warehouse copy, fed over RabbitMQ (ADR 0029): no call to Inventory.
        Fleet = new ServiceFactory<FleetService>(Login("smartspare_fleet", passwords.Fleet!), _rabbit.GetConnectionString());
        Gateway = new GatewayFactory(this);

        // Start every service, so each consumer's queue exists before anything is published.
        _ = Identity.Server;
        _ = Inventory.Server;
        _ = Fleet.Server;

        // Fleet's warehouse copy is filled as after a deployment (ADR 0029): the Inventory service runs the publish job
        // and its event reaches the Fleet service over RabbitMQ.
        await using (var scope = Inventory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetServices<IJob>().Single(j => j.Name == "inventory.warehouse-catalog-publish").RunAsync(Ct);
        }

        await WaitForWarehouseCopyAsync();
    }

    private async Task WaitForWarehouseCopyAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using var connection = new NpgsqlConnection(_migrator);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM fleet.warehouse_snapshot s JOIN inventory.warehouse w ON w.id = s.warehouse_id AND w.code = s.code", connection);
            if ((long)(await command.ExecuteScalarAsync(Ct))! >= 7)
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException("WarehouseCatalogChanged did not reach the Fleet service within 30 s.");
    }

    public async ValueTask DisposeAsync()
    {
        await Gateway.DisposeAsync();
        await Fleet.DisposeAsync();
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
            INSERT INTO iam.position (code, name) VALUES ('T_' || upper(left(replace(@id::text, '-', ''), 12)), 'Test position');
            INSERT INTO iam.position_role (position_id, role_id)
            SELECT p.id, r.id FROM iam.position p JOIN iam.role r ON r.code = ANY(@roles) WHERE p.code = 'T_' || upper(left(replace(@id::text, '-', ''), 12));
            INSERT INTO iam.employee (id, code, full_name, position_id)
            SELECT @id, 'E' || lpad((coalesce((SELECT max(substr(code, 2)::int) FROM iam.employee), 0) + 1)::text, 6, '0'), 'Test User', p.id
            FROM iam.position p WHERE p.code = 'T_' || upper(left(replace(@id::text, '-', ''), 12));
            INSERT INTO iam.app_user (id, employee_id, user_name, password_hash) VALUES (@id, @id, @name, @hash);
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", userName);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("roles", roles);
        await command.ExecuteNonQueryAsync(Ct);

        // Inventory's employee copy learns the employee as after a deployment (ADR 0029 amendment 2): the Identity service
        // runs its publish job and the event reaches the Inventory service over RabbitMQ.
        await using (var scope = Identity.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetServices<IJob>().Single(j => j.Name == "identity.employee-publish").RunAsync(Ct);
        }

        await WaitForAsync("SELECT count(*) FROM inventory.employee_snapshot WHERE employee_id = @id AND account_id = @id", id, "EmployeeDirectoryChanged");
        return id;
    }

    private async Task WaitForAsync(string countSql, Guid id, string what)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using var connection = new NpgsqlConnection(_migrator);
            await connection.OpenAsync(Ct);
            await using var count = new NpgsqlCommand(countSql, connection);
            count.Parameters.AddWithValue("id", id);
            if ((long)(await count.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException($"{what} did not reach the consumer within 30 s.");
    }

    /// <summary>A browser signed in through the gateway, with the antiforgery header of the signed-in user.</summary>
    public async Task<HttpClient> SignInAsync(string userName)
    {
        var client = new HttpClient(new CookieContainerHandler(Gateway.Server.CreateHandler())) { BaseAddress = new Uri("https://localhost") };
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenResponse>(new Uri("/bff/antiforgery", UriKind.Relative), Ct))!;
        client.DefaultRequestHeaders.Add(token.HeaderName, token.RequestToken);
        using var login = await client.PostAsJsonAsync(new Uri("/bff/login", UriKind.Relative), new LoginRequest(userName, Password, false), Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The token is bound to the user, so a signed-in client fetches a new one, as the web client does.
        var signedIn = (await client.GetFromJsonAsync<AntiforgeryTokenResponse>(new Uri("/bff/antiforgery", UriKind.Relative), Ct))!;
        client.DefaultRequestHeaders.Remove(signedIn.HeaderName);
        client.DefaultRequestHeaders.Add(signedIn.HeaderName, signedIn.RequestToken);
        return client;
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
    public sealed class ServiceFactory<TEntryPoint>(string connectionString, string broker, Action<IServiceCollection>? testServices = null)
        : WebApplicationFactory<TEntryPoint>
        where TEntryPoint : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.Jwks)}", PublicJwks());
            builder.UseSetting("ConnectionStrings:smartspare", connectionString);
            builder.UseSetting("ConnectionStrings:messaging", broker);
            if (testServices is not null)
            {
                builder.ConfigureTestServices(testServices);
            }
        }
    }

    /// <summary>The gateway; <c>identity</c>, <c>inventory</c> and <c>fleet</c> are the real service hosts, every other service answers 503.</summary>
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
                "fleet" => topology.Fleet.Server.CreateHandler(),
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
