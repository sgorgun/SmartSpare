// SmartSpare.DbMigrator — applies db/migrations/V*.sql and db/seed/R*.sql to PostgreSQL.
// Same rules as db/scripts/migrate.sh (used locally); this one runs as a CI/CD step before
// the App Service deployment:
//   * versioned scripts run once, in order; repeatable scripts re-run when their checksum changes
//   * each script + its journal row in ONE transaction (all or nothing)
//   * SHA-256 checksum; an edited, already applied V-script stops the deployment (exit 2)
//   * pg_advisory_xact_lock: two pipelines can never migrate concurrently
//   * the login role (smartspare_migrator) is not a superuser; scripts run as SET LOCAL ROLE owner
// Connection string: env SMARTSPARE_MIGRATOR_CONNECTION (from Azure Key Vault in the pipeline).
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

const string OwnerRole = "smartspare_owner";
var connectionString = Environment.GetEnvironmentVariable("SMARTSPARE_MIGRATOR_CONNECTION")
    ?? throw new InvalidOperationException("SMARTSPARE_MIGRATOR_CONNECTION is not set.");

var assembly = Assembly.GetExecutingAssembly();
var scripts = assembly.GetManifestResourceNames()
    .Select(name => new Script(name, assembly))
    .OrderBy(s => s.Kind)                 // all V before all R
    .ThenBy(s => s.Version)
    .ToList();

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using var connection = await dataSource.OpenConnectionAsync();

await ExecuteAsync(connection, null, $"""
    SET ROLE {OwnerRole};
    CREATE SCHEMA IF NOT EXISTS meta;
    CREATE TABLE IF NOT EXISTS meta.schema_migration (
        script varchar(200) PRIMARY KEY, kind char(1) NOT NULL CHECK (kind IN ('V','R')),
        version integer NOT NULL, description varchar(200) NOT NULL,
        checksum char(64) NOT NULL CHECK (checksum ~ '^[0-9a-f]{{64}}$'),
        applied_by name NOT NULL DEFAULT session_user, applied_at timestamptz NOT NULL DEFAULT now(),
        execution_ms integer NOT NULL CHECK (execution_ms >= 0));
    RESET ROLE;
    """);

var applied = new Dictionary<string, string>();
await using (var cmd = new NpgsqlCommand("SELECT script, checksum FROM meta.schema_migration", connection))
await using (var reader = await cmd.ExecuteReaderAsync())
    while (await reader.ReadAsync()) applied[reader.GetString(0)] = reader.GetString(1);

int count = 0;
foreach (var script in scripts)
{
    if (applied.TryGetValue(script.FileName, out var checksum))
    {
        if (checksum == script.Checksum) continue;
        if (script.Kind == 'V')
        {
            Console.Error.WriteLine($"ERROR: {script.FileName} was modified after it had been applied (checksum drift). " +
                                    "Never edit an applied migration - add a new V-script.");
            return 2;
        }
    }

    var sw = Stopwatch.StartNew();
    await using var tx = await connection.BeginTransactionAsync();
    await ExecuteAsync(connection, tx, "SELECT pg_advisory_xact_lock(hashtext('smartspare.migrations'))");
    await ExecuteAsync(connection, tx, $"SET LOCAL ROLE {OwnerRole}; SET LOCAL search_path = public;");
    await ExecuteAsync(connection, tx, script.Sql);
    await using (var journal = new NpgsqlCommand("""
        INSERT INTO meta.schema_migration (script, kind, version, description, checksum, execution_ms)
        VALUES (@script, @kind, @version, @description, @checksum, @ms)
        ON CONFLICT (script) DO UPDATE SET checksum = EXCLUDED.checksum, applied_at = now(),
                                           applied_by = session_user, execution_ms = EXCLUDED.execution_ms
        """, connection, tx))
    {
        journal.Parameters.AddWithValue("script", script.FileName);
        journal.Parameters.AddWithValue("kind", script.Kind.ToString());
        journal.Parameters.AddWithValue("version", script.Version);
        journal.Parameters.AddWithValue("description", script.Description);
        journal.Parameters.AddWithValue("checksum", script.Checksum);
        journal.Parameters.AddWithValue("ms", (int)sw.ElapsedMilliseconds);
        await journal.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    Console.WriteLine($"applied  {script.FileName}  ({sw.ElapsedMilliseconds} ms)");
    count++;
}
Console.WriteLine($"Done: {count} applied, {scripts.Count - count} already up to date.");
return 0;

static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql)
{
    await using var cmd = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 0 };
    await cmd.ExecuteNonQueryAsync();
}

sealed class Script
{
    public Script(string resourceName, Assembly assembly)
    {
        FileName = resourceName[(resourceName.IndexOf('/') + 1)..];       // "migrations/V001__x.sql" -> "V001__x.sql"
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        Checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();   // = sha256sum of the file
        Sql = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        Kind = FileName[0];
        var parts = FileName[1..^4].Split("__", 2);
        Version = int.Parse(parts[0]);
        Description = parts[1].Replace('_', ' ');
    }
    public string FileName { get; }
    public string Checksum { get; }
    public string Sql { get; }
    public char Kind { get; }
    public int Version { get; }
    public string Description { get; }
}
