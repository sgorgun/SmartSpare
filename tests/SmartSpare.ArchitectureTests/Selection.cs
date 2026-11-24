using System.Reflection;
using NetArchTest.Rules;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// Guards against rules that pass vacuously. A NetArchTest rule over an empty type selection always
/// succeeds, so a typo in a namespace, a moved folder or a renamed module would silently switch a rule off.
/// Every rule therefore states which types it expects to inspect, and fails when the selection is empty
/// although the source tree says there is code to check.
/// </summary>
internal static class Selection
{
    /// <summary>All types of <paramref name="assembly"/>; an assembly always has at least one.</summary>
    public static Types Of(Assembly assembly)
    {
        var types = Types.InAssembly(assembly);
        types.GetTypes().ShouldNotBeEmpty($"{assembly.GetName().Name} has no types to check");
        return types;
    }

    /// <summary>
    /// The types of <paramref name="module"/> in <c>{module namespace}.{area}</c>, for example <c>Domain</c>.
    /// Fails when the module's <c>{area}/</c> folder contains C# files but the selection is empty.
    /// </summary>
    public static PredicateList InArea(Module module, string area)
    {
        ArgumentNullException.ThrowIfNull(module);
        var selection = Types.InAssembly(module.Implementation).That().ResideInNamespaceStartingWith($"{module.Namespace}.{area}");
        ExpectTypesIfSourcesExist(selection, Solution.SourceFiles(module, area), $"{module.Name}/{area}");
        return selection;
    }

    /// <summary>Fails when <paramref name="selection"/> is empty.</summary>
    public static PredicateList NotEmpty(PredicateList selection, string description)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection.GetTypes().ShouldNotBeEmpty($"the rule for {description} selects no types");
        return selection;
    }

    private static void ExpectTypesIfSourcesExist(PredicateList selection, IReadOnlyList<string> sources, string description)
    {
        if (sources.Count > 0)
        {
            selection.GetTypes().ShouldNotBeEmpty(
                $"{description} has {sources.Count} source file(s) but the rule selects no types; check the namespace");
        }
    }
}
