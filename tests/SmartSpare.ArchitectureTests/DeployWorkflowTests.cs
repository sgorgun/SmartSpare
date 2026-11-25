namespace SmartSpare.ArchitectureTests;

/// <summary>
/// The deploy workflows' path filters (STAGE_4_PLAN 8.3): every deployable project redeploys when it changes, so nothing
/// under <c>src/</c> silently never reaches production. The filters are generated (tools/generate_deploy_workflows.py);
/// <c>tools/check.sh deploy-paths</c> fails when they are out of date.
/// </summary>
public sealed class DeployWorkflowTests
{
    /// <summary>Projects that are never deployed: the local orchestration and the single host of development and tests.</summary>
    private static readonly string[] LocalOnly = ["SmartSpare.AppHost", "SmartSpare.Host"];

    private static readonly string[] Services = ["gateway", "identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    [Fact]
    public void There_is_a_deploy_workflow_per_service_and_one_for_the_migrations()
    {
        foreach (var name in Services.Append("migrations"))
        {
            File.Exists(Workflow(name)).ShouldBeTrue($"deploy-{name}.yml");
        }
    }

    [Fact]
    public void Every_deployable_project_is_covered_by_a_deploy_workflow()
    {
        var filters = Services.Append("migrations").SelectMany(name => PathFilters(Workflow(name))).ToList();
        var projects = Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !LocalOnly.Contains(Path.GetFileNameWithoutExtension(p)))
            .Select(p => Path.GetRelativePath(Solution.RootDirectory, p).Replace('\\', '/'))
            .ToList();

        projects.ShouldNotBeEmpty();
        projects.Where(project => !filters.Any(filter => Matches(filter, project))).ShouldBeEmpty("projects no deploy workflow watches");
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void A_service_redeploys_when_any_project_it_references_changes(string service)
    {
        var filters = PathFilters(Workflow(service)).ToList();
        var host = service == "gateway" ? "SmartSpare.Gateway" : $"SmartSpare.Services.{char.ToUpperInvariant(service[0])}{service[1..]}";

        foreach (var project in Closure(host))
        {
            filters.ShouldContain(f => Matches(f, project), $"deploy-{service}.yml does not watch {project}");
        }
    }

    public static TheoryData<string> ServiceNames => [.. Services];

    private static string Workflow(string name) => Path.Combine(Solution.RootDirectory, ".github", "workflows", $"deploy-{name}.yml");

    /// <summary>The <c>paths:</c> entries of the push trigger (one quoted entry per line, as the generator writes them).</summary>
    private static IEnumerable<string> PathFilters(string workflow) =>
        File.ReadLines(workflow)
            .SkipWhile(l => l.Trim() != "paths:")
            .Skip(1)
            .TakeWhile(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l.Trim()[2..].Trim('\'', '"'));

    private static bool Matches(string filter, string path) =>
        filter.EndsWith("/**", StringComparison.Ordinal)
            ? path.StartsWith(filter[..^2], StringComparison.Ordinal)
            : string.Equals(filter, path, StringComparison.Ordinal);

    /// <summary>The project and everything it references, as repository-relative .csproj paths.</summary>
    private static IEnumerable<string> Closure(string projectName)
    {
        var start = Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), projectName + ".csproj", SearchOption.AllDirectories).Single();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([Path.GetFullPath(start)]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var include in System.Xml.Linq.XDocument.Load(current).Descendants("ProjectReference").Select(e => e.Attribute("Include")!.Value))
            {
                stack.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, include.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        return seen.Select(p => Path.GetRelativePath(Solution.RootDirectory, p).Replace('\\', '/'));
    }
}
