using System.Data.Common;
using System.Globalization;
using System.Text;
using Npgsql;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// Runs <c>db/bootstrap/000_bootstrap_cluster.sql</c> without psql. The script is kept as a psql
/// script because the server admin also runs it by hand on Azure; this interpreter supports exactly
/// the psql features it uses: <c>\set</c> (ignored), <c>:'variable'</c> (quoted literal) and
/// <c>\gexec</c> (run every value of the result as a statement). Every statement runs in autocommit
/// mode, as with psql, because <c>CREATE DATABASE</c> cannot run inside a transaction.
/// </summary>
public static class BootstrapScript
{
    private const string ResourceName = "bootstrap/000_bootstrap_cluster.sql";

    public static string Load()
    {
        using var stream = typeof(BootstrapScript).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Splits the script into commands; each is plain SQL or a query whose results are executed (\gexec).</summary>
    public static IReadOnlyList<(string Sql, bool GExec)> Parse(string script, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(variables);

        var commands = new List<(string, bool)>();
        var buffer = new StringBuilder();

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.StartsWith(@"\set", StringComparison.Ordinal))
            {
                continue;
            }

            var code = StripTrailingComment(line);
            if (code.TrimEnd().EndsWith(@"\gexec", StringComparison.Ordinal))
            {
                buffer.AppendLine(code.TrimEnd()[..^@"\gexec".Length]);
                commands.Add((Substitute(buffer.ToString(), variables).Trim(), true));
                buffer.Clear();
                continue;
            }

            buffer.AppendLine(code);
            if (code.TrimEnd().EndsWith(';'))
            {
                commands.Add((Substitute(buffer.ToString(), variables).Trim(), false));
                buffer.Clear();
            }
        }

        if (buffer.ToString().Trim().Length > 0)
        {
            throw new FormatException("The bootstrap script ends with an unterminated statement.");
        }

        return commands;
    }

    /// <summary>Executes the script on an admin connection (server admin / local superuser).</summary>
    public static async Task ExecuteAsync(NpgsqlConnection connection, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        foreach (var (sql, gexec) in Parse(Load(), variables))
        {
            if (!gexec)
            {
                await ExecuteNonQueryAsync(connection, sql, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var generated = new List<string>();
            await using (var command = new NpgsqlCommand(sql, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (!await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false))
                        {
                            generated.Add(reader.GetString(i));
                        }
                    }
                }
            }

            foreach (var statement in generated)
            {
                await ExecuteNonQueryAsync(connection, statement, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL comes from the reviewed bootstrap script; variables are quoted literals.
        command.CommandText = sql;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>psql's <c>:'name'</c>: the value as a single-quoted SQL literal.</summary>
    private static string Substitute(string sql, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            sql = sql.Replace($":'{name}'", "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal);
        }

        var unresolved = sql.IndexOf(":'", StringComparison.Ordinal);
        if (unresolved >= 0)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "The bootstrap script uses a variable that was not supplied near: {0}", sql[unresolved..Math.Min(sql.Length, unresolved + 30)]));
        }

        return sql;
    }

    /// <summary>Removes a trailing <c>-- comment</c> that is outside single quotes.</summary>
    private static string StripTrailingComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '\'')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && line[i] == '-' && line[i + 1] == '-')
            {
                return line[..i];
            }
        }

        return line;
    }
}
