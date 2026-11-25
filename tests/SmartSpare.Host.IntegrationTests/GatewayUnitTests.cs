extern alias gateway;

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Bff;
using gateway::SmartSpare.Gateway;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>Gateway configuration without Docker (STAGE_4_PLAN section 10, unit level).</summary>
public sealed class GatewayUnitTests
{
    [Fact]
    public void The_gateway_lists_exactly_the_catalogue_modules() =>
        GatewayRoutes.Modules.ShouldBe(SmartSpare.ModuleCatalog.ModuleCatalog.CreateInstallers().Select(m => m.Name));

    [Fact]
    public void Locally_only_loopback_proxies_are_trusted()
    {
        var options = new ForwardedHeadersOptions();

        GatewayForwardedHeaders.Configure(options, Config());

        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.ForwardLimit.ShouldBe(1);
        options.KnownIPNetworks.ShouldNotBeEmpty();
        options.KnownProxies.ShouldContain(IPAddress.IPv6Loopback);
    }

    [Fact]
    public void In_azure_the_ingress_is_trusted_for_the_rightmost_value_only()
    {
        var options = new ForwardedHeadersOptions();

        GatewayForwardedHeaders.Configure(options, Config((GatewayForwardedHeaders.TrustIngressKey, "true")));

        options.ForwardLimit.ShouldBe(1);
        options.KnownIPNetworks.ShouldBeEmpty();
        options.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Behind_the_ingress_a_forged_client_address_is_ignored()
    {
        // The ingress appends the real client as the rightmost value; anything the client sent stays to the left.
        var options = new ForwardedHeadersOptions();
        GatewayForwardedHeaders.Configure(options, Config((GatewayForwardedHeaders.TrustIngressKey, "true")));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("100.100.0.7");
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 203.0.113.9";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, Options.Create(options))
            .Invoke(context);

        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.9"));
        context.Request.Scheme.ShouldBe("https");
    }

    [Theory]
    [InlineData(null, null, null, GatewayDataProtection.Store.Default)]
    [InlineData(null, null, "/data/keys", GatewayDataProtection.Store.Directory)]
    [InlineData("https://a.blob.core.windows.net/dp/keys.xml", "https://v.vault.azure.net/keys/dp", null, GatewayDataProtection.Store.BlobWithKeyVault)]
    public void Data_protection_is_chosen_by_environment(string? blob, string? keyVault, string? directory, GatewayDataProtection.Store expected) =>
        GatewayDataProtection.Select(Config(
            (GatewayDataProtection.BlobUriKey, blob), (GatewayDataProtection.KeyVaultKeyIdKey, keyVault), (GatewayDataProtection.KeysDirectoryKey, directory)))
            .ShouldBe(expected);

    [Fact]
    public void Keys_in_blob_storage_are_never_unprotected() =>
        Should.Throw<InvalidOperationException>(() => GatewayDataProtection.Select(Config((GatewayDataProtection.BlobUriKey, "https://a.blob.core.windows.net/dp/keys.xml"))));

    [Fact]
    public void A_session_token_is_reused_until_30_seconds_before_it_expires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var keys = new InternalTokenKeyRing(Options.Create(new InternalTokenOptions { SigningKey = TestKeys.SigningKeyPem }));
        var cache = new SessionTokenCache(new InternalTokenIssuer(keys, time), time);
        var user = User(AppRoles.InventoryPlanner);

        var first = cache.GetToken(user, "s1");
        time.Advance(InternalTokenDefaults.Lifetime - SessionTokenCache.RenewBefore - TimeSpan.FromSeconds(1));
        var reused = cache.GetToken(user, "s1");
        time.Advance(TimeSpan.FromSeconds(1));
        var renewed = cache.GetToken(user, "s1");

        reused.ShouldBe(first);
        renewed.ShouldNotBe(first);
        cache.GetToken(user, "s2").ShouldNotBe(renewed, "each session has its own token");
        cache.GetToken(User(AppRoles.Administrator), "s1").ShouldNotBe(renewed, "changed claims get a new token at once");
    }

    /// <summary>
    /// Business endpoints are versioned (ADR 0012): every module endpoint under <c>/api</c> is either under
    /// <c>/api/v1/{module}</c> or explicitly operational, so nothing reaches the gateway's unversioned ops route by
    /// accident (product owner, 25 Nov 2026).
    /// </summary>
    [Fact]
    public void Every_unversioned_module_endpoint_is_marked_operational()
    {
        using var host = new FakeIdentityHostFactory();

        var unversioned = host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } p && p.StartsWith("/api/", StringComparison.Ordinal) && !p.StartsWith("/api/v1/", StringComparison.Ordinal))
            .ToList();

        unversioned.ShouldNotBeEmpty("the module pings are unversioned operational endpoints");
        unversioned.Where(e => e.Metadata.GetMetadata<OperationalEndpointMetadata>() is null).Select(e => e.RoutePattern.RawText).ShouldBeEmpty();
    }

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(AppClaimTypes.Subject, "7f6b0c2e-0000-0000-0000-000000000001"), new Claim(AppClaimTypes.Name, "u"), new Claim(AppClaimTypes.Role, role)], "test"));

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}
