using NetArchTest.Rules;

namespace SmartSpare.ArchitectureTests;

/// <summary>The guard of <see cref="Selection"/>: rules cannot pass on an empty type selection.</summary>
public sealed class SelectionTests
{
    [Fact]
    public void An_empty_selection_fails()
    {
        var nothing = Types.InAssembly(Solution.SharedKernel).That().ResideInNamespace("SmartSpare.DoesNotExist");

        Should.Throw<ShouldAssertException>(() => Selection.NotEmpty(nothing, "a namespace that does not exist"));
    }

    [Fact]
    public void The_identity_domain_rules_inspect_the_aggregate()
    {
        var domain = Selection.InArea(Solution.Get("Identity"), "Domain").GetTypes().Select(t => t.Name).ToList();

        domain.ShouldContain("AppUser");
        domain.ShouldContain("UserName");
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Every_module_area_with_sources_has_types(string name)
    {
        var module = Solution.Get(name);

        foreach (var area in new[] { "Domain", "Features", "Infrastructure" })
        {
            if (Solution.SourceFiles(module, area).Count > 0)
            {
                Selection.InArea(module, area).GetTypes().ShouldNotBeEmpty($"{name}/{area}");
            }
        }
    }
}
