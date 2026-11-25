using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Every service host starts with exactly its own module (STAGE_4_PLAN 4d acceptance): only its routes, internal-token
/// authentication as the only scheme, and its connection pool capped (8.5). No database or broker is needed to start.
/// </summary>
public sealed class ServiceStartTests
{
    public static TheoryData<string> Services => ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    [Theory]
    [MemberData(nameof(Services))]
    public void A_service_maps_only_its_own_module(string module)
    {
        using var factory = Create(module);

        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        routes.ShouldContain($"/api/{module}/ping");
        routes.ShouldAllBe(r => r.StartsWith($"/api/{module}/", StringComparison.Ordinal)
                                || r.StartsWith($"/internal/v1/{module}/", StringComparison.Ordinal)
                                || r.StartsWith("/health/", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_service_authenticates_internal_tokens_only(string module)
    {
        using var factory = Create(module);

        var schemes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();

        (await schemes.GetAllSchemesAsync()).Select(s => s.Name).ShouldBe([InternalTokenDefaults.Scheme]);
    }

    [Fact]
    public void A_service_pools_at_most_four_connections_unless_configured()
    {
        using var factory = Create("inventory", connectionString: "Host=db;Database=smartspare;Username=smartspare_inventory");

        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString(PersistenceRegistration.ConnectionStringName);

        new Npgsql.NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize.ShouldBe(4);
    }

    private static IServiceFactory Create(string module, string? connectionString = null) => module switch
    {
        "identity" => new ServiceFactory<SmartSpare.Services.Identity.IdentityService>(connectionString),
        "inventory" => new ServiceFactory<SmartSpare.Services.Inventory.InventoryService>(connectionString),
        "fleet" => new ServiceFactory<SmartSpare.Services.Fleet.FleetService>(connectionString),
        "procurement" => new ServiceFactory<SmartSpare.Services.Procurement.ProcurementService>(connectionString),
        "forecasting" => new ServiceFactory<SmartSpare.Services.Forecasting.ForecastingService>(connectionString),
        "reporting" => new ServiceFactory<SmartSpare.Services.Reporting.ReportingService>(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
    };

    private interface IServiceFactory : IDisposable
    {
        IServiceProvider Services { get; }
    }

    /// <summary>
    /// A service host without a database or a broker. With a connection string, the outbox dispatcher is left out of the
    /// started host, so nothing connects.
    /// </summary>
    private sealed class ServiceFactory<TEntryPoint>(string? connectionString) : WebApplicationFactory<TEntryPoint>, IServiceFactory
        where TEntryPoint : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.Jwks)}", IdentityServiceFactory.PublicJwks());
            builder.UseSetting("ConnectionStrings:messaging", "amqp://guest:guest@127.0.0.1:1/");
            if (connectionString is not null)
            {
                builder.UseSetting("ConnectionStrings:smartspare", connectionString);
                builder.ConfigureServices(services =>
                {
                    foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                    {
                        services.Remove(hosted);
                    }
                });
            }
        }
    }
}
