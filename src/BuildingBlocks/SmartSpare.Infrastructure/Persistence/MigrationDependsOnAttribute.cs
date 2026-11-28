namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Declares, on the migration that needs it, that another module's migration must have run first when both are pending
/// in the same run of the MigrationRunner (ADR 0020 amendment). Within one module EF Core keeps the order; across
/// modules the runner applies the contexts in catalogue order and holds a migration with this attribute until the
/// migration it names has been applied. Put it on a separate partial declaration of the migration class (for example
/// <c>20261127163529_DropLegacyAccess.DependsOn.cs</c>), so the migration file itself never changes once applied.
/// <c>MigrationDependencyTests</c> proves every declaration names an existing migration, the dependencies have no
/// cycle and the runner's order respects them.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MigrationDependsOnAttribute(string module, string migration) : Attribute
{
    /// <summary>The other module's database name, e.g. <c>reporting</c>.</summary>
    public string Module { get; } = module;

    /// <summary>The full migration id, e.g. <c>20261127154602_LegacyAccessToAssignments</c>.</summary>
    public string Migration { get; } = migration;
}
