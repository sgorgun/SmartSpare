using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace SmartSpare.Gateway;

/// <summary>Forwarded headers of the gateway (STAGE_4_PLAN 2.5, #48).</summary>
public static class GatewayForwardedHeaders
{
    public const string TrustIngressKey = "ForwardedHeaders:TrustContainerAppsIngress";

    /// <summary>
    /// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, rightmost value only (<c>ForwardLimit = 1</c>). By default only
    /// loopback proxies are trusted. With <see cref="TrustIngressKey"/> (Azure only) the trusted lists are cleared: the
    /// gateway has no public address, so every request comes through the Container Apps ingress, which appends the
    /// client's address as the rightmost value.
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        if (configuration.GetValue<bool>(TrustIngressKey))
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
    }
}

/// <summary>Where the gateway keeps its Data Protection key ring (ADR 0016 section 6, STAGE_4_PLAN 2.4).</summary>
public static class GatewayDataProtection
{
    public const string ApplicationName = "SmartSpare";

    public const string BlobUriKey = "DataProtection:BlobUri";

    public const string KeyVaultKeyIdKey = "DataProtection:KeyVaultKeyId";

    public const string KeysDirectoryKey = "DataProtection:KeysDirectory";

    public enum Store
    {
        /// <summary>Azure: a blob, its key wrapped by Key Vault, both through the gateway's Managed Identity.</summary>
        BlobWithKeyVault,

        /// <summary>Locally: a directory on the AppHost's data volume, so restarts keep the cookies valid.</summary>
        Directory,

        /// <summary>Tests: the default per-process ring.</summary>
        Default,
    }

    public static Store Select(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var blob = configuration[BlobUriKey];
        var keyVault = configuration[KeyVaultKeyIdKey];
        if (!string.IsNullOrWhiteSpace(blob) || !string.IsNullOrWhiteSpace(keyVault))
        {
            return !string.IsNullOrWhiteSpace(blob) && !string.IsNullOrWhiteSpace(keyVault)
                ? Store.BlobWithKeyVault
                : throw new InvalidOperationException($"Set both {BlobUriKey} and {KeyVaultKeyIdKey}: keys in Blob Storage are always protected by Key Vault.");
        }

        return string.IsNullOrWhiteSpace(configuration[KeysDirectoryKey]) ? Store.Default : Store.Directory;
    }

    public static IDataProtectionBuilder AddGatewayDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        switch (Select(configuration))
        {
            case Store.BlobWithKeyVault:
                var credential = new DefaultAzureCredential();
                builder.PersistKeysToAzureBlobStorage(new Uri(configuration[BlobUriKey]!), credential)
                    .ProtectKeysWithAzureKeyVault(new Uri(configuration[KeyVaultKeyIdKey]!), credential);
                break;
            case Store.Directory:
                // Unprotected at rest, accepted for local development like the single host (#22).
                builder.PersistKeysToFileSystem(new DirectoryInfo(configuration[KeysDirectoryKey]!));
                break;
            case Store.Default:
                break;
        }

        return builder;
    }
}

/// <summary>
/// <c>GET /warmup</c> (STAGE_4_PLAN 2.4, 8.7): wakes every service from zero by calling its readiness endpoint over
/// internal ingress in parallel, and reports each one's status and time. Anonymous, at most 2 calls a minute.
/// </summary>
public static class WarmupEndpoint
{
    public const string HttpClientName = "warmup";

    public const string RateLimitPolicy = "warmup";

    public static IServiceCollection AddWarmup(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, _ =>
            RateLimitPartition.GetFixedWindowLimiter("warmup", _ => new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) })));
        return services;
    }

    public static void MapWarmup(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/warmup", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
            {
                using var client = clients.CreateClient(HttpClientName);
                var results = await Task.WhenAll(GatewayRoutes.Modules.Select(m => PingAsync(client, m, cancellationToken))).ConfigureAwait(false);
                return TypedResults.Ok(results);
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .ExcludeFromDescription();

    private static async Task<WarmupResult> PingAsync(HttpClient client, string module, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await client.GetAsync(new Uri($"http://{module}/health/ready"), cancellationToken).ConfigureAwait(false);
            return new(module, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(module, (int)HttpStatusCode.ServiceUnavailable, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public sealed record WarmupResult(string Service, int Status, long Milliseconds);
}
