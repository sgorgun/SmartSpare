using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// Which databases one run migrates (ADR 0020 §3). Without a module: all, in catalogue order. With <c>--module</c>:
/// the Platform, then that module, and only if no module earlier in the catalogue has pending migrations, so
/// per-service deployments can't apply changes out of order.
/// </summary>
public static class MigrationScope
{
    public static async Task<IReadOnlyList<IModuleDatabase>> SelectAsync(
        IReadOnlyList<IModuleDatabase> databases,
        string? module,
        Func<IModuleDatabase, Task<IReadOnlyList<string>>> pendingMigrations)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(pendingMigrations);
        if (string.IsNullOrWhiteSpace(module))
        {
            return databases;
        }

        var index = -1;
        for (var i = 0; i < databases.Count; i++)
        {
            if (string.Equals(databases[i].Name, module, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Unknown module '{module}'. Known: {string.Join(", ", databases.Select(d => d.Name))}.");
        }

        if (index == 0)
        {
            return [databases[0]];
        }

        // The Platform (index 0) is migrated by this run; every module between it and the target must be up to date.
        var behind = new List<string>();
        for (var i = 1; i < index; i++)
        {
            var pending = await pendingMigrations(databases[i]).ConfigureAwait(false);
            if (pending.Count > 0)
            {
                behind.Add($"{databases[i].Name} ({pending.Count} pending)");
            }
        }

        if (behind.Count > 0)
        {
            throw new InvalidOperationException(
                $"Refusing to migrate '{databases[index].Name}' before earlier modules: {string.Join(", ", behind)}. "
                + "Run their migrations first, or run without --module.");
        }

        return [databases[0], databases[index]];
    }
}
