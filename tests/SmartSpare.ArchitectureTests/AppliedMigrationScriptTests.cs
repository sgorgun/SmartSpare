using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// #152 part 1: a SQL resource of a migration is never edited once the migration is merged, because an existing database
/// has already run it and would never receive the change, or would fail later with a raw SQL error (42P07 on
/// <c>fleet.v_service_kit_shortage</c>). <c>db/sql/applied.sha256</c> records the hash of every script a migration
/// references; a change becomes a new script and a new migration (<c>CREATE OR REPLACE</c>, <c>DROP ... IF EXISTS</c>).
/// New scripts are appended with <c>python tools/sql_manifest.py</c>.
/// </summary>
public sealed partial class AppliedMigrationScriptTests
{
    private static readonly string SqlRoot = Path.Combine(Solution.RootDirectory, "db", "sql");

    private static readonly string ManifestPath = Path.Combine(SqlRoot, "applied.sha256");

    [Fact]
    public void Every_script_of_a_migration_is_in_the_manifest()
    {
        var manifest = ReadManifest();

        var missing = ReferencedScripts()
            .Where(r => !manifest.ContainsKey(r.Script))
            .Select(r => $"{Hash(r.Script)}  {r.Script}  {r.Migration}")
            .ToList();

        missing.ShouldBeEmpty(
            "new SQL scripts are not in db/sql/applied.sha256; run `python tools/sql_manifest.py` or append:\n"
            + string.Join('\n', missing));
    }

    [Fact]
    public void No_script_of_an_applied_migration_has_changed()
    {
        var changed = ReadManifest()
            .Where(e => File.Exists(FullPath(e.Key)) && Hash(e.Key) != e.Value.Hash)
            .Select(e => $"{e.Key} belongs to the applied migration {e.Value.Migration}; add a new script and migration instead (never edit an applied script, #152)")
            .ToList();

        changed.ShouldBeEmpty(string.Join('\n', changed));
    }

    [Fact]
    public void No_script_of_an_applied_migration_was_removed()
    {
        var removed = ReadManifest()
            .Where(e => !File.Exists(FullPath(e.Key)))
            .Select(e => $"{e.Key} belongs to the applied migration {e.Value.Migration} and must stay in db/sql/")
            .ToList();

        removed.ShouldBeEmpty(string.Join('\n', removed));
    }

    [Fact]
    public void The_manifest_lists_only_scripts_that_a_migration_references()
    {
        var referenced = ReferencedScripts().Select(r => r.Script).ToHashSet(StringComparer.Ordinal);

        var orphans = ReadManifest().Keys.Where(s => !referenced.Contains(s)).ToList();

        orphans.ShouldBeEmpty($"listed in db/sql/applied.sha256 but referenced by no migration: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void The_hash_ignores_line_endings()
    {
        Hash(Encoding.UTF8.GetBytes("select 1;\r\nselect 2;\r\n"))
            .ShouldBe(Hash(Encoding.UTF8.GetBytes("select 1;\nselect 2;\n")));
    }

    /// <summary>Every <c>SqlResource(..., "folder/file.sql")</c> call in a migration, with the first migration that runs it.</summary>
    private static List<(string Script, string Migration)> ReferencedScripts() =>
        [.. Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "Migrations" && !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .SelectMany(f => ResourceCall().Matches(File.ReadAllText(f))
                .Select(m => (Script: m.Groups["path"].Value, Migration: MigrationName(f))))
            .GroupBy(r => r.Script, StringComparer.Ordinal)
            .Select(g => g.First())];

    /// <summary><c>20261116222230_InitialFleet.cs</c> → <c>InitialFleet</c>.</summary>
    private static string MigrationName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var underscore = name.IndexOf('_', StringComparison.Ordinal);
        return underscore < 0 ? name : name[(underscore + 1)..];
    }

    private static Dictionary<string, (string Hash, string Migration)> ReadManifest()
    {
        File.Exists(ManifestPath).ShouldBeTrue("db/sql/applied.sha256 is missing");

        return File.ReadAllLines(ManifestPath)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => p[1], p => (p[0], p[2]), StringComparer.Ordinal);
    }

    private static string FullPath(string script) => Path.Combine(SqlRoot, script.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(string script) => Hash(File.ReadAllBytes(FullPath(script)));

    /// <summary>SHA-256 of the text with CRLF normalised to LF, so a Windows checkout and CI agree.</summary>
    private static string Hash(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    [GeneratedRegex("""SqlResource\([^,]+,\s*"(?<path>[a-z]+/[A-Za-z0-9_]+\.sql)"\)""")]
    private static partial Regex ResourceCall();
}
