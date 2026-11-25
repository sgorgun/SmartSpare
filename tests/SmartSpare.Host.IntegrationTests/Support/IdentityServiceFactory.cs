using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartSpare.Bff;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Services.Identity;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// The real Identity service host (<c>SmartSpare.Services.Identity</c>, STAGE_4_PLAN 4d) in memory. It holds only the
/// public JWKS of <see cref="TestKeys.SigningKeyPem"/>, like a service in production, and no broker: Identity has no
/// subscriptions, and the credential checks publish nothing.
/// </summary>
public sealed class IdentityServiceFactory(string? connectionString, TimeProvider? time = null) : WebApplicationFactory<IdentityService>
{
    /// <summary>The gateway's token minting with the test key; tokens use the system clock, as JwtBearer validates them.</summary>
    internal static InternalTokenIssuer GatewayIssuer { get; } = new(
        new InternalTokenKeyRing(Options.Create(new InternalTokenOptions { SigningKey = TestKeys.SigningKeyPem })), TimeProvider.System);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.Jwks)}", PublicJwks());
        builder.UseSetting("ConnectionStrings:messaging", "amqp://guest:guest@127.0.0.1:1/");
        if (connectionString is not null)
        {
            builder.UseSetting("ConnectionStrings:smartspare", connectionString);
        }

        if (time is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(time));
        }
    }

    /// <summary>
    /// <see cref="HttpCredentialVerifier"/> over this host, sending the gateway's service token as the gateway will (4e).
    /// </summary>
    internal HttpCredentialVerifier Verifier() => new(CreateDefaultClient(new ServiceTokenHandler(GatewayIssuer)));

    /// <summary>A client that sends <paramref name="bearer"/>, or nothing.</summary>
    internal HttpClient Client(string? bearer)
    {
        var client = CreateClient();
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        }

        return client;
    }

    internal static string PublicJwks()
    {
        using var keys = new InternalTokenKeyRing(Options.Create(new InternalTokenOptions { SigningKey = TestKeys.SigningKeyPem }));
        return keys.PublicJwks();
    }
}
