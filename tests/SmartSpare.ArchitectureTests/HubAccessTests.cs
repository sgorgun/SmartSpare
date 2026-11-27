namespace SmartSpare.ArchitectureTests;

/// <summary>
/// Hub access guards writes only (ADR 0026 decision 5, STAGE_7_PLAN 1.2): reads are never scoped by hub, as decided in
/// stage 3. <c>util.require_hub</c> may appear only in the listed write functions, and the C# rule <c>HubAccess</c>
/// never in a query or read model.
/// </summary>
public sealed class HubAccessTests
{
    /// <summary>The SQL write functions that check hub access; a new one is added here deliberately.</summary>
    private static readonly string[] WriteFunctions = ["inventory.dispatch_transfer", "inventory.receive_transfer"];

    [Fact]
    public void Only_write_functions_call_util_require_hub()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "db", "sql"), "*.sql", SearchOption.AllDirectories))
        {
            foreach (var statement in File.ReadAllText(file).Split("CREATE ", StringSplitOptions.None).Skip(1))
            {
                if (!statement.Contains("util.require_hub(", StringComparison.Ordinal)
                    || statement.StartsWith("OR REPLACE FUNCTION util.require_hub", StringComparison.Ordinal))
                {
                    continue;
                }

                var isWrite = WriteFunctions.Any(f =>
                    statement.StartsWith($"OR REPLACE FUNCTION {f}(", StringComparison.Ordinal)
                    || statement.StartsWith($"FUNCTION {f}(", StringComparison.Ordinal));
                if (!isWrite)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {statement[..Math.Min(80, statement.Length)]}");
                }
            }
        }

        offenders.ShouldBeEmpty("util.require_hub guards writes only; reads are never scoped by hub");
    }

    [Fact]
    public void No_query_or_read_model_uses_the_hub_rule()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src", "Modules"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Queries{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f =>
            {
                var code = File.ReadAllText(f);
                return code.Contains("HubAccess", StringComparison.Ordinal) || code.Contains("require_hub", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty("a list or detail never hides another hub's data");
    }
}
