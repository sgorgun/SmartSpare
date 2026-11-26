using System.Text.RegularExpressions;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// The unit-test metric of business logic is defined by layer (#63, product owner, 27 November 2026): each module's
/// Domain and Features count, its Infrastructure does not. These rules keep the definition honest: the coverage
/// configuration can never exclude Domain or Features, and Features never execute SQL themselves, so moving code to
/// Infrastructure cannot hide business rules from the metric while data access stays where it belongs.
/// </summary>
public sealed partial class CoverageDefinitionTests
{
    /// <summary>The raw-SQL entry points: <c>ExecuteSql*</c> and <c>SqlQuery*</c> live on these types, plus Npgsql itself.</summary>
    private static readonly string[] RawSql =
    [
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions",
        "Npgsql",
    ];

    private static readonly string CheckScript = File.ReadAllText(Path.Combine(Solution.RootDirectory, "tools", "check.sh"));

    [Fact]
    public void Domain_and_Features_are_part_of_the_business_logic_metric()
    {
        var files = Filters("BUSINESS_FILES");

        files.ShouldContain("+*Domain*");
        files.ShouldContain("+*Features*");
        files.Where(f => f.StartsWith('-')).ShouldAllBe(
            f => !f.Contains("Domain", StringComparison.OrdinalIgnoreCase) && !f.Contains("Features", StringComparison.OrdinalIgnoreCase),
            "no exclusion may remove Domain or Features from the metric");
        Filters("BUSINESS_ASSEMBLIES").ShouldContain("+SmartSpare.Modules.*");
    }

    [Fact]
    public void Infrastructure_is_measured_only_by_the_all_code_figures()
    {
        Filters("BUSINESS_FILES").ShouldNotContain(f => f.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase) && f.StartsWith('+'));
        Filters("ALL_ASSEMBLIES").ShouldContain("+SmartSpare.*");
    }

    [Fact]
    public void Our_code_does_not_opt_out_of_coverage_by_attribute()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("ExcludeFromCodeCoverage", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty("the metric is defined by layer, not per class");
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Features_do_not_execute_sql(string name)
    {
        var module = Solution.Get(name);

        var result = Selection.InArea(module, "Features").ShouldNot().HaveDependencyOnAny(RawSql).GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Features of {name} run SQL directly; move it to Infrastructure: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    /// <summary>The ReportGenerator filters assigned to <paramref name="variable"/> in tools/check.sh.</summary>
    private static List<string> Filters(string variable)
    {
        var match = Regex.Match(CheckScript, $"^{variable}=\"([^\"]*)\"", RegexOptions.Multiline);
        match.Success.ShouldBeTrue($"{variable} is defined in tools/check.sh");
        return [.. match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }
}
