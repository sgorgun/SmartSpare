using Npgsql;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// Settings of the runner (configuration section <c>Migrations</c>; the admin connection may also come
/// from <c>ConnectionStrings:postgres</c>, which Aspire provides). Passwords come from user-secrets,
/// Aspire parameters or CI variables, never from files in the repository.
/// </summary>
public sealed class MigrationRunnerOptions
{
    public const string SectionName = "Migrations";

    /// <summary>Server admin / local superuser, used only to run the cluster bootstrap. Optional.</summary>
    public string? AdminConnectionString { get; set; }

    /// <summary>Connection of <c>smartspare_migrator</c>. Optional: derived from the admin connection.</summary>
    public string? ConnectionString { get; set; }

    public string Database { get; set; } = "smartspare";

    /// <summary>NOLOGIN role that owns every object; the migrator acts as it (MODULE_MAPPING 5.5).</summary>
    public string OwnerRole { get; set; } = "smartspare_owner";

    public BootstrapPasswords Passwords { get; set; } = new();

    /// <summary>
    /// Connection used to apply migrations: the migrator login with the session role set at start-up
    /// (<c>Options=-c role=smartspare_owner</c>), so every object and history table is owned by the
    /// owner role, exactly like <c>SET ROLE</c> in db/scripts/migrate.sh.
    /// </summary>
    public string BuildMigratorConnectionString()
    {
        NpgsqlConnectionStringBuilder builder;
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(AdminConnectionString))
        {
            builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Database = Database,
                Username = "smartspare_migrator",
                Password = Passwords.Migrator ?? throw new InvalidOperationException("Migrations:Passwords:Migrator is required."),
            };
        }
        else
        {
            throw new InvalidOperationException(
                "Configure Migrations:ConnectionString (migrator) or ConnectionStrings:postgres / Migrations:AdminConnectionString (admin).");
        }

        if (!string.IsNullOrWhiteSpace(OwnerRole))
        {
            builder.Options = $"-c role={OwnerRole}";
        }

        builder.ApplicationName = "smartspare-migration-runner";
        return builder.ConnectionString;
    }
}

/// <summary>Passwords the bootstrap gives to the login roles (psql variables of 000_bootstrap_cluster.sql).</summary>
public sealed class BootstrapPasswords
{
    public string? Migrator { get; set; }

    public string? Api { get; set; }

    public string? Jobs { get; set; }

    public string? Reporting { get; set; }

    public IReadOnlyDictionary<string, string> ToVariables() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["migrator_pwd"] = Migrator ?? throw Missing(nameof(Migrator)),
        ["api_pwd"] = Api ?? throw Missing(nameof(Api)),
        ["jobs_pwd"] = Jobs ?? throw Missing(nameof(Jobs)),
        ["report_pwd"] = Reporting ?? throw Missing(nameof(Reporting)),
    };

    private static InvalidOperationException Missing(string name) =>
        new($"Migrations:Passwords:{name} is required to bootstrap the server.");
}
