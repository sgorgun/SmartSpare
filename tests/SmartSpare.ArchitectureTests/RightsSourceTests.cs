namespace SmartSpare.ArchitectureTests;

/// <summary>
/// No path edits rights directly (ADR 0026, STAGE_7_PLAN 1.7): Identity's projection of the responsibility assignments
/// is written only by the <c>ResponsibilitiesChanged</c> consumer, the role template only through the <c>Position</c>
/// aggregate, and no code writes a role, brand or hub onto an account (the legacy tables are gone).
/// </summary>
public sealed class RightsSourceTests
{
    private static readonly string[] SourceFiles =
    [
        .. Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)),
    ];

    [Theory]
    [InlineData("INSERT INTO iam.employee_responsibility", "ApplyResponsibilitiesConsumer.cs")]
    [InlineData("DELETE FROM iam.employee_responsibility", "ApplyResponsibilitiesConsumer.cs")]
    [InlineData("INSERT INTO iam.responsibility_source", "ApplyResponsibilitiesConsumer.cs")]
    public void Only_the_consumer_writes_the_projection(string statement, string allowedFile) =>
        Writers(statement).ShouldBe([allowedFile], $"'{statement}' outside the ResponsibilitiesChanged consumer would edit rights directly");

    [Theory]
    [InlineData("iam.position_role")]
    [InlineData("iam.user_role")]
    [InlineData("iam.user_brand")]
    [InlineData("home_warehouse_id")]
    public void No_code_writes_roles_brands_or_hubs_with_sql(string target) =>
        SourceFiles.Where(f => File.ReadAllText(f).Contains(target, StringComparison.Ordinal)
                               && (File.ReadAllText(f).Contains("INSERT INTO " + target, StringComparison.Ordinal)
                                   || File.ReadAllText(f).Contains("UPDATE " + target, StringComparison.Ordinal)
                                   || File.ReadAllText(f).Contains("SET " + target, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ShouldBeEmpty($"{target} is changed only through the organisation (positions, assignments)");

    private static List<string?> Writers(string statement) =>
        [.. SourceFiles.Where(f => File.ReadAllText(f).Contains(statement, StringComparison.Ordinal)).Select(Path.GetFileName).Distinct()];
}
