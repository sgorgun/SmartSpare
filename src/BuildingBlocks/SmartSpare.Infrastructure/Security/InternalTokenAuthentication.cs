using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Infrastructure.Security;

/// <summary>Validation of internal tokens with JwtBearer (ADR 0016 section 2): the single host and every service host.</summary>
public static class InternalTokenAuthentication
{
    /// <summary>
    /// Adds the JwtBearer scheme <see cref="InternalTokenDefaults.Scheme"/>: ES256 only, fixed issuer and audience,
    /// expiry required, 30 s clock skew, keys from <see cref="InternalTokenKeyRing"/>. Claims keep their short names
    /// (<see cref="AppClaimTypes"/>), so the principal equals the cookie principal.
    /// </summary>
    public static AuthenticationBuilder AddInternalToken(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddInternalTokenKeys();

        builder.AddJwtBearer(InternalTokenDefaults.Scheme, _ => { });
        builder.Services.AddOptions<JwtBearerOptions>(InternalTokenDefaults.Scheme)
            .Configure<InternalTokenKeyRing>((options, keys) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = InternalTokenDefaults.Issuer,
                    ValidAudience = InternalTokenDefaults.Audience,
                    IssuerSigningKeys = keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = InternalTokenDefaults.ClockSkew,
                    NameClaimType = AppClaimTypes.Name,
                    RoleClaimType = AppClaimTypes.Role,
                    AuthenticationType = InternalTokenDefaults.AuthenticationType,
                };
                options.Events = new JwtBearerEvents { OnTokenValidated = ExpandPermissions };
            });

        return builder;
    }

    /// <summary>
    /// Turns the token's <see cref="AppClaimTypes.PermissionSet"/> bitset back into <see cref="AppClaimTypes.Permission"/>
    /// claims (ADR 0028), so policies read the same claims whichever scheme authenticated the request.
    /// </summary>
    internal static Task ExpandPermissions(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is ClaimsIdentity identity && identity.FindFirst(AppClaimTypes.PermissionSet) is { } set)
        {
            identity.AddClaims(PermissionBitset.Decode(set.Value).Select(p => new Claim(AppClaimTypes.Permission, p)));
        }

        return Task.CompletedTask;
    }

    /// <summary>Binds <see cref="InternalTokenOptions"/> and registers the <see cref="InternalTokenKeyRing"/> once.</summary>
    public static IServiceCollection AddInternalTokenKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(InternalTokenKeyRing)))
        {
            return services;
        }

        services.AddOptions<InternalTokenOptions>().BindConfiguration(InternalTokenOptions.SectionName);
        services.AddSingleton<InternalTokenKeyRing>();
        return services;
    }
}
