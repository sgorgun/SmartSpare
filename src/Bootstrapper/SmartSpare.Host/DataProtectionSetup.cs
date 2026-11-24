using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity;

namespace SmartSpare.Host;

/// <summary>Configuration section <c>DataProtection</c>: how the host protects its key ring at rest.</summary>
internal sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary>PFX file of the key-encryption certificate (with <see cref="CertificatePassword"/>, a secret).</summary>
    public string? CertificatePath { get; init; }

    public string? CertificatePassword { get; init; }

    /// <summary>Thumbprint of a key-encryption certificate in the CurrentUser\My store (alternative to a file).</summary>
    public string? CertificateThumbprint { get; init; }

    /// <summary>Azure Key Vault key protection. Off until stage 10, when the production key vault exists.</summary>
    public bool UseKeyVault { get; init; }
}

/// <summary>
/// ASP.NET Core Data Protection of the single host (STAGE_3_PLAN section 5, #22). The host owns the configuration: the
/// application name, where the keys live and how they are protected at rest. The Identity module only supplies the
/// table (<see cref="IdentityModuleInstaller.PersistDataProtectionKeys"/>), so every instance that shares the database
/// shares the key ring and accepts the others' cookies and antiforgery tokens.
/// </summary>
internal static class DataProtectionSetup
{
    public const string ApplicationName = "SmartSpare";

    public static IDataProtectionBuilder AddSmartSpareDataProtection(this WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        // Without a database (host tests) the keys stay in the default per-instance ring.
        if (builder.Configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName) is not null)
        {
            IdentityModuleInstaller.PersistDataProtectionKeys(dataProtection);
        }

        var settings = builder.Configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new();
        if (settings.UseKeyVault)
        {
            throw new InvalidOperationException(
                "DataProtection:UseKeyVault is not available yet: Key Vault key protection is set up with the production environment (stage 10).");
        }

        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            dataProtection.ProtectKeysWithCertificate(
                X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword));
        }
        else if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
        {
            dataProtection.ProtectKeysWithCertificate(settings.CertificateThumbprint);
        }

        // Otherwise the keys are stored unprotected, which is accepted for local development (#22). The single host is
        // not deployed; the gateway's key ring goes to Blob Storage protected by Key Vault (ADR 0016 section 6).
        return dataProtection;
    }
}
