using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SmartSpare.Bff;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// The real single host (<c>Program</c>) in memory. It holds an in-memory internal-token signing key, uses a fake clock
/// for the cookie and session checks, and either a database (<see cref="ConnectionString"/>) with the real Identity
/// module or a <see cref="FakeCredentialVerifier"/> with no database at all.
/// </summary>
public class HostFactory : WebApplicationFactory<Program>
{
    /// <summary>Starts at the real time, because JwtBearer validates token lifetimes against the system clock.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    internal FakeCredentialVerifier? Verifier { get; init; }

    public string? ConnectionString { get; init; }

    public bool WithSigningKey { get; init; } = true;

    /// <summary>More test services, registered last; for example an HTTP <see cref="ICredentialVerifier"/>.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    /// <summary>The host environment; <see langword="null"/> keeps the factory's default (Development).</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Keeps the Quartz scheduler of the single host (off by default: tests run jobs themselves).</summary>
    public bool WithJobScheduler { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (EnvironmentName is not null)
        {
            builder.UseEnvironment(EnvironmentName);
        }

        if (WithSigningKey)
        {
            builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.SigningKey)}", TestKeys.SigningKeyPem);
        }

        if (ConnectionString is not null)
        {
            builder.UseSetting("ConnectionStrings:smartspare", ConnectionString);
        }

        // Tests run jobs themselves, at their own clock; the scheduler only where a test asks for it.
        builder.UseSetting("Jobs:Enabled", WithJobScheduler ? "true" : "false");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIp>();
            if (Verifier is not null)
            {
                services.AddSingleton<ICredentialVerifier>(Verifier);
                services.AddSingleton<IRolePermissions, FakeRolePermissions>();
            }

            ExtraServices?.Invoke(services);
        });
    }

    internal Browser NewBrowser() => Browser.Over(Server.CreateHandler(), TestClientIp.Unique());

    /// <summary>An internal token minted exactly as the gateway will mint it (ADR 0016).</summary>
    internal string Token(ClaimsPrincipal user) =>
        Services.GetRequiredService<InternalTokenIssuer>().CreateToken(user, Guid.NewGuid().ToString("N"));
}

/// <summary>The real host with a <see cref="FakeCredentialVerifier"/> and no database.</summary>
public sealed class FakeIdentityHostFactory : HostFactory
{
    public FakeIdentityHostFactory() => Verifier = new FakeCredentialVerifier();

    internal FakeCredentialVerifier Users => Verifier!;
}
