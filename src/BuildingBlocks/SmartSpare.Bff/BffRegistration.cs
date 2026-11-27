using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Bff;

/// <summary>Registration of the BFF in a host: the single host now, the gateway in stage 4 (ADR 0016 section 4).</summary>
public static class BffRegistration
{
    /// <summary>
    /// The BFF services every host that issues the cookie needs: antiforgery (header <c>X-XSRF-TOKEN</c>, cookie
    /// <c>__Host-SmartSpare-AF</c>), the sign-in rate limit, the cookie events and the internal-token issuer.
    /// Authentication schemes are added separately (<see cref="AddBffCookie"/>, <see cref="AddSingleHostAuthentication"/>).
    /// </summary>
    public static IServiceCollection AddBff(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddInternalTokenKeys();
        services.TryAddSingleton<InternalTokenIssuer>();
        services.TryAddScoped<BffCookieEvents>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = BffDefaults.AntiforgeryHeaderName;
            options.Cookie.Name = BffDefaults.AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
        });

        services.AddOptions<SessionCheckOptions>()
            .BindConfiguration(SessionCheckOptions.Section)
            .Validate(session => session.CheckInterval > TimeSpan.Zero && session.CheckInterval <= TimeSpan.FromMinutes(5),
                "Bff:Session:CheckInterval must be positive and at most 5 minutes (ADR 0026).")
            .ValidateOnStart();

        services.AddOptions<LoginRateLimitOptions>()
            .BindConfiguration(LoginRateLimitOptions.Section)
            .Validate(limit => limit.PermitLimit > 0 && limit.Window > TimeSpan.Zero,
                "Bff:LoginRateLimit needs a positive PermitLimit and Window.")
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partitioned by client IP. Behind the platform's proxy the IP must come from forwarded headers of known
            // proxies (#48); the limiter is per instance (#49). Account brute force is stopped by lockout.
            options.AddPolicy(BffDefaults.LoginRateLimitPolicy, http =>
            {
                var limit = http.RequestServices.GetRequiredService<IOptions<LoginRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit.PermitLimit,
                        Window = limit.Window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });

            // API documentation (ADR 0019 §4): public, so limited per client IP like sign-in, but generously.
            options.AddPolicy(ApiDocumentation.Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            // The status-code pages write the 429 ProblemDetails; here only Retry-After is added.
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        // The documents change only with a deployment (ADR 0019 §4). The default policy never caches authenticated requests.
        services.AddOutputCache(options => options.AddPolicy(ApiDocumentation.Policy, policy => policy.Expire(TimeSpan.FromMinutes(10))));

        return services;
    }

    /// <summary>
    /// The auth cookie <c>__Host-SmartSpare</c> (STAGE_3_PLAN section 1.3): HttpOnly, Secure, SameSite=Strict, <c>Path=/</c>,
    /// 8 h sliding expiry, 401/403 instead of redirects for <c>/api</c> and <c>/bff</c>, session check every 5 minutes.
    /// </summary>
    public static AuthenticationBuilder AddBffCookie(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The session check and the ticket expiry use the application's clock (replaceable in tests).
        builder.Services.AddOptions<CookieAuthenticationOptions>(BffDefaults.CookieScheme)
            .Configure<TimeProvider>((options, time) => options.TimeProvider = time);

        return builder.AddCookie(BffDefaults.CookieScheme, options =>
        {
            options.Cookie.Name = BffDefaults.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = BffDefaults.SessionLifetime;
            options.SlidingExpiration = true;
            options.LoginPath = "/login";
            options.EventsType = typeof(BffCookieEvents);
        });
    }

    /// <summary>
    /// Authentication and authorization of the single host (ADR 0016 section 4): the policy scheme
    /// <see cref="BffDefaults.PolicyScheme"/> is the default and forwards a request with <c>Authorization: Bearer</c> to the
    /// internal-token scheme and every other request to the cookie. Module endpoints never name a scheme. Policies come
    /// are permissions (ADR 0028), added to the request by <see cref="PermissionClaimsTransformation"/>, plus the brand-scope handler.
    /// </summary>
    public static IServiceCollection AddSingleHostAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(BffDefaults.PolicyScheme)
            .AddPolicyScheme(BffDefaults.PolicyScheme, BffDefaults.PolicyScheme, options =>
                options.ForwardDefaultSelector = http => IsBearerRequest(http.Request) ? InternalTokenDefaults.Scheme : BffDefaults.CookieScheme)
            .AddBffCookie()
            .AddInternalToken();

        services.AddPermissionAuthorization();
        services.TryAddSingleton<RolePermissionCache>();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddAuthorization(ServiceAuthentication.AddGatewayOnlyPolicy);
        services.AddBrandScopeAuthorization();
        return services;
    }

    /// <summary>
    /// The security part of the pipeline in the order of STAGE_3_PLAN section 8.1: routing, the rate limiter (after
    /// routing, for the endpoint policy of <c>/bff/login</c>), authentication, authorization.
    /// </summary>
    /// <summary>
    /// The gateway's authentication (ADR 0016 section 4): the cookie is the only scheme. It never accepts an internal
    /// token; it mints them. Policies are the same as everywhere.
    /// </summary>
    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(BffDefaults.CookieScheme).AddBffCookie();
        services.AddPermissionAuthorization();
        services.TryAddSingleton<RolePermissionCache>();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddBrandScopeAuthorization();
        return services;
    }

    public static IApplicationBuilder UseBff(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        return app;
    }

    private static bool IsBearerRequest(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
}
