using SmartSpare.Infrastructure.Persistence;
using SmartSpare.MigrationRunner;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>
/// The cross-module migration dependencies, declared with <see cref="MigrationDependsOnAttribute"/> next to the migration
/// that needs them (ADR 0020 amendment): each names an existing migration of another module, together with EF Core's
/// order inside a module they have no cycle, and the runner's plan respects every one of them, on a fresh database and
/// on databases migrated part of the way. No database needed.
/// </summary>
public sealed class MigrationDependencyTests
{
    private static readonly IReadOnlyList<IModuleDatabase> Catalogue = ModuleCatalog.ModuleCatalog.Databases();

    private static IEnumerable<(IModuleDatabase Database, string Migration, string Module, string Awaited)> Declared() =>
        Catalogue.SelectMany(d => d.Dependencies.SelectMany(m => m.Value.Select(a => (d, m.Key, a.Module, a.Migration))));

    [Fact]
    public void There_are_declared_dependencies_and_each_names_an_existing_migration_of_another_module()
    {
        Declared().Count().ShouldBeGreaterThanOrEqualTo(3, "DropLegacyAccess and BrandMove declare theirs");
        foreach (var (database, migration, module, awaited) in Declared())
        {
            module.ShouldNotBe(database.Name, $"{migration}: EF Core already orders the migrations of one module");
            var other = Catalogue.SingleOrDefault(d => d.Name == module);
            other.ShouldNotBeNull($"{database.Name} {migration} names the unknown module '{module}'");
            other.Migrations.ShouldContain(awaited, $"{database.Name} {migration} names the unknown migration {module} {awaited}");
        }
    }

    [Fact]
    public void Together_with_the_order_inside_each_module_the_dependencies_have_no_cycle() =>
        FindCycle(Graph(Catalogue)).ShouldBeNull();

    [Fact]
    public void The_cycle_check_finds_a_cycle()
    {
        var graph = new Dictionary<string, List<string>> { ["a"] = ["b"], ["b"] = ["c"], ["c"] = ["a"] };

        FindCycle(graph).ShouldNotBeNull();
    }

    [Fact]
    public void On_a_fresh_database_the_runner_applies_every_migration_after_the_ones_it_depends_on() =>
        AssertPlanRespectsDependencies(Catalogue.ToDictionary(d => d, d => d.Migrations.ToList()));

    [Fact]
    public void On_databases_migrated_part_of_the_way_the_runner_still_respects_every_dependency()
    {
        // Cut-offs at each awaited migration: everything older is applied (in every module), the rest is pending.
        foreach (var cutoff in Declared().Select(d => d.Awaited).Distinct())
        {
            AssertPlanRespectsDependencies(Catalogue.ToDictionary(d => d, d => d.Migrations.Where(m => string.CompareOrdinal(m, cutoff) >= 0).ToList()));
        }
    }

    private static void AssertPlanRespectsDependencies(Dictionary<IModuleDatabase, List<string>> pending)
    {
        var all = pending.SelectMany(p => p.Value.Select(m => (p.Key.Name, m))).ToHashSet();
        var runs = DatabaseMigrator.Plan(Catalogue, pending);

        var order = runs.SelectMany(r => r.Migrations.Select(m => (r.Database.Name, m))).ToList();
        order.ToHashSet().SetEquals(all).ShouldBeTrue("every pending migration is planned, none is left waiting");
        foreach (var (database, migration, module, awaited) in Declared())
        {
            var at = order.IndexOf((database.Name, migration));
            var before = order.IndexOf((module, awaited));
            if (at >= 0 && before >= 0)
            {
                before.ShouldBeLessThan(at, $"{module} {awaited} runs before {database.Name} {migration}");
            }
        }
    }

    /// <summary>Nodes are "module/migration"; an edge points to what must run first.</summary>
    private static Dictionary<string, List<string>> Graph(IReadOnlyList<IModuleDatabase> databases)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            var migrations = database.Migrations;
            for (var i = 0; i < migrations.Count; i++)
            {
                var edges = graph[$"{database.Name}/{migrations[i]}"] = [];
                if (i > 0)
                {
                    edges.Add($"{database.Name}/{migrations[i - 1]}");
                }

                if (database.Dependencies.TryGetValue(migrations[i], out var awaited))
                {
                    edges.AddRange(awaited.Select(a => $"{a.Module}/{a.Migration}"));
                }
            }
        }

        return graph;
    }

    /// <summary>A cycle as its nodes, or <see langword="null"/>.</summary>
    private static string? FindCycle(Dictionary<string, List<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = on the stack, 2 = done
        var stack = new List<string>();

        string? Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in graph.GetValueOrDefault(node) ?? [])
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    return string.Join(" -> ", stack.SkipWhile(n => n != next).Append(next));
                }

                if (state.GetValueOrDefault(next) == 0 && Visit(next) is { } cycle)
                {
                    return cycle;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            return null;
        }

        return graph.Keys.Where(n => state.GetValueOrDefault(n) == 0).Select(Visit).FirstOrDefault(c => c is not null);
    }
}
