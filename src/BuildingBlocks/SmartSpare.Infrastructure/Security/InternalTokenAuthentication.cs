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
            });

        return builder;
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
