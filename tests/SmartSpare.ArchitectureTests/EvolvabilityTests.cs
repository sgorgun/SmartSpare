using System.Xml.Linq;
using NetArchTest.Rules;

namespace SmartSpare.ArchitectureTests;

/// <summary>Seams to volatile technology from ADR 0012: ML, cloud SDKs and a framework-free Domain.</summary>
public sealed class EvolvabilityTests
{
    private const string MachineLearningNamespace = "Microsoft.ML";
    private const string MachineLearningPackagePrefix = "Microsoft.ML";
    private const string ForecastingProject = "SmartSpare.Modules.Forecasting";
    private const string ForecastingInfrastructureNamespace = "SmartSpare.Modules.Forecasting.Infrastructure";

    private static readonly string[] CloudSdkNamespaces = ["Azure", "Microsoft.Azure", "Amazon", "Google.Cloud", "Google.Apis"];

    private static readonly string[] CloudSdkPackagePrefixes = ["Azure.", "Microsoft.Azure.", "AWSSDK.", "Amazon.", "Google.Cloud.", "Google.Apis."];

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void MachineLearning_IsUsedOnlyInForecastingInfrastructure(string name)
    {
        var module = Solution.Get(name);

        var implementationTypes = Types.InAssembly(module.Implementation).That();
        var outsideTheSeam = module.Implementation.GetName().Name == ForecastingProject
            ? implementationTypes.DoNotResideInNamespaceStartingWith(ForecastingInfrastructureNamespace)
            : implementationTypes.ResideInNamespaceStartingWith(module.Namespace);

        var result = outsideTheSeam.ShouldNot().HaveDependencyOn(MachineLearningNamespace).GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));

        var contracts = Types.InAssembly(module.Contracts).ShouldNot().HaveDependencyOn(MachineLearningNamespace).GetResult();
        contracts.IsSuccessful.ShouldBeTrue(Describe(contracts));
    }

    [Fact]
    public void MachineLearningPackages_AreReferencedOnlyByForecasting()
    {
        var offenders = AllProjects()
            .Where(p => p.Name != ForecastingProject)
            .Where(p => p.Packages.Any(IsMachineLearningPackage))
            .Select(p => p.Name);

        offenders.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Module_ReferencesNoCloudSdk(string name)
    {
        var module = Solution.Get(name);

        foreach (var assembly in new[] { module.Implementation, module.Contracts })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(CloudSdkNamespaces).GetResult();
            result.IsSuccessful.ShouldBeTrue(Describe(result));

            assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .ShouldNotContain(n => CloudSdkPackagePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));

            PackageReferences(assembly.GetName().Name!)
                .ShouldNotContain(p => CloudSdkPackagePrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Domain_DependsOnlyOnBaseLibraryAndSharedKernel(string name)
    {
        var module = Solution.Get(name);

        var result = Types.InAssembly(module.Implementation)
            .That().ResideInNamespaceStartingWith(module.Namespace + ".Domain")
            .Should().OnlyHaveDependenciesOn("System", "SmartSpare.SharedKernel", module.Namespace + ".Domain", CoverageTrackerNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <summary>
    /// CI collects coverage (<c>--collect "XPlat Code Coverage"</c>); coverlet rewrites every method to call its
    /// injected tracker type. That is instrumentation, not a dependency of the domain code.
    /// </summary>
    private const string CoverageTrackerNamespace = "Coverlet.Core.Instrumentation.Tracker";

    private static bool IsMachineLearningPackage(string package) =>
        package.Equals(MachineLearningPackagePrefix, StringComparison.Ordinal)
        || package.StartsWith(MachineLearningPackagePrefix + ".", StringComparison.Ordinal);

    private static IEnumerable<string> PackageReferences(string projectName)
    {
        var path = Directory
            .EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), projectName + ".csproj", SearchOption.AllDirectories)
            .Single();

        return ReadPackages(path);
    }

    private static IEnumerable<(string Name, IReadOnlyList<string> Packages)> AllProjects() =>
        Directory
            .EnumerateFiles(Path.Combine(Solution.RootDirectory, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(p => (Path.GetFileNameWithoutExtension(p), (IReadOnlyList<string>)[.. ReadPackages(p)]));

    private static IEnumerable<string> ReadPackages(string csprojPath) =>
        XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>();

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
