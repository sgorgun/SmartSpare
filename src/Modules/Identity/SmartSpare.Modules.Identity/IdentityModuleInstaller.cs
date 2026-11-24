using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Identity;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Features.Ping;

namespace SmartSpare.Modules.Identity;

/// <summary>Registers the Identity module in the host. The only public type of the module.</summary>
public sealed class IdentityModuleInstaller : IModuleInstaller
{
    public string Name => IdentityModule.Name;

    /// <summary>
    /// PBKDF2 iteration count of new password hashes: the OWASP recommendation for PBKDF2-HMAC-SHA512
    /// (STAGE_3_PLAN section 2.3). Older hashes keep verifying and are rehashed at the next sign-in.
    /// </summary>
    public const int PasswordHashIterations = 220_000;

    public IModuleDatabase Database => IdentityDatabase.Instance;

    /// <summary>
    /// The one password-hashing configuration of SmartSpare: ASP.NET Core Identity V3 (PBKDF2-HMAC-SHA512)
    /// with <see cref="PasswordHashIterations"/>. Used by the host (through <see cref="AddServices"/>) and by
    /// <c>MigrationRunner set-demo-passwords</c>, so demo users get exactly the hashes the application creates.
    /// </summary>
    public static void ConfigurePasswordHasher(PasswordHasherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
        options.IterationCount = PasswordHashIterations;
    }

    /// <summary>
    /// Stores the Data Protection key ring in <c>iam.data_protection_keys</c> (#22). Called by the host, which owns the
    /// Data Protection configuration (application name, key protection); the context type stays internal to the module.
    /// </summary>
    public static IDataProtectionBuilder PersistDataProtectionKeys(IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.PersistKeysToDbContext<IdentityDbContext>();
    }

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(IdentityModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        IdentityDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), IdentityDbContext.HistorySchema);

        services.Configure<PasswordHasherOptions>(ConfigurePasswordHasher);
        services.TryAddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

        // ASP.NET Core Identity over the iam tables (STAGE_3_PLAN section 2, ADR 0014). Used only behind
        // ICredentialVerifier: the module checks credentials and sessions, the BFF issues the cookie.
        services.TryAddSingleton(TimeProvider.System);

        // The token providers need Data Protection. The module only requires it; where the keys live and how they
        // are protected is the host's decision (STAGE_3_PLAN section 5), see PersistDataProtectionKeys.
        services.AddDataProtection();
        services.AddSingleton<ILookupNormalizer, LowerInvariantLookupNormalizer>();
        services.AddIdentityCore<AppUser>(ConfigureIdentity)
            .AddUserStore<AppUserStore>()
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserConfirmation<AppUser>, ActiveUserConfirmation>();
        services.AddScoped<ICredentialVerifier, CredentialVerifier>();
    }

    /// <summary>
    /// Identity options of SmartSpare. Lockout comes from <see cref="LockoutPolicy.Default"/>, the one source of
    /// these values; Identity runs the lockout algorithm with them (section 2.4).
    /// </summary>
    internal static void ConfigureIdentity(IdentityOptions options)
    {
        options.ClaimsIdentity.UserIdClaimType = AppClaimTypes.Subject;
        options.ClaimsIdentity.UserNameClaimType = AppClaimTypes.Name;
        options.ClaimsIdentity.RoleClaimType = AppClaimTypes.Role;
        options.ClaimsIdentity.SecurityStampClaimType = IdentityClaimTypes.SecurityStamp;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = LockoutPolicy.Default.MaxFailedAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutPolicy.Default.Duration;

        // Deactivated and anonymised users are refused through ActiveUserConfirmation (section 2.6).
        options.SignIn.RequireConfirmedAccount = true;

        // New passwords only (user administration, #46); length matters more than character classes (section 2.7).
        options.Password.RequiredLength = 12;
        options.Password.RequiredUniqueChars = 4;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Identity");
        PingEndpoint.Map(group);
    }
}
