using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Runs the SQL parts of a migration that EF Core cannot model (functions, triggers, partitions,
/// grants, seed data; ADR 0004). The scripts live in <c>db/sql/&lt;folder&gt;/</c> and are embedded
/// into the owning assembly by <c>Directory.Build.targets</c> (property <c>SmartSpareSqlFolder</c>).
/// </summary>
public static class SqlResourceMigrationExtensions
{
    /// <summary>Executes an embedded script, e.g. <c>SqlResource(assembly, "platform/0001_util_helpers.sql")</c>.</summary>
    /// <remarks>
    /// The whole file is sent as one command, so dollar-quoted function bodies are never split.
    /// A script used by a released migration must not change; a change is a new file and a new migration.
    /// </remarks>
    public static OperationBuilder<SqlOperation> SqlResource(this MigrationBuilder migrationBuilder, Assembly assembly, string path)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        return migrationBuilder.Sql(ReadSqlResource(assembly, path));
    }

    /// <summary>Reads an embedded script; throws if it is missing, so a typo fails the migration at once.</summary>
    public static string ReadSqlResource(Assembly assembly, string path)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = "sql/" + path.Replace('\\', '/').TrimStart('/');
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded SQL resource '{name}' was not found in {assembly.GetName().Name}. "
                + "Check the file under db/sql/ and the project's SmartSpareSqlFolder property.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
