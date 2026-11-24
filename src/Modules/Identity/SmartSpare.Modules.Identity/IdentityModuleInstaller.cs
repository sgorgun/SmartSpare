using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
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

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(IdentityModuleInstaller).Assembly;
        services.AddHandlersFromAssembly(assembly);
        IdentityDatabase.Instance.AddTo(services, assembly, configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName), IdentityDbContext.HistorySchema);

        services.Configure<PasswordHasherOptions>(ConfigurePasswordHasher);
        services.TryAddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"/api/{Name}").WithTags("Identity");
        PingEndpoint.Map(group);
    }
}
