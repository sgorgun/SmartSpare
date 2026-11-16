using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Persistence.Platform;
using SmartSpare.Modules.Fleet;
using SmartSpare.Modules.Forecasting;
using SmartSpare.Modules.Identity;
using SmartSpare.Modules.Inventory;
using SmartSpare.Modules.Procurement;
using SmartSpare.Modules.Reporting;

namespace SmartSpare.ModuleCatalog;

/// <summary>
/// The single, ordered list of modules of this deployment, used by the host (registration,
/// endpoints) and by the migration runner (local, CI). Extracting a module into its own service
/// means removing it here and listing it in the new service's catalogue (ADR 0002).
/// </summary>
public static class ModuleCatalog
{
    /// <summary>
    /// Module installers in dependency order. Reporting stays last: its marts read every schema
    /// (ADR 0011, MODULE_MAPPING section 5.2).
    /// </summary>
    public static IReadOnlyList<IModuleInstaller> CreateInstallers()
    {
        IModuleInstaller[] modules =
        [
            new IdentityModuleInstaller(),
            new InventoryModuleInstaller(),
            new FleetModuleInstaller(),
            new ProcurementModuleInstaller(),
            new ForecastingModuleInstaller(),
            new ReportingModuleInstaller(),
        ];

        var duplicate = modules.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one module installer is named '{duplicate.Key}'.");
        }

        return modules;
    }

    /// <summary>
    /// Databases in migration order: the Platform first (extensions, util helpers, ref data and the
    /// audit trigger every module uses), then the modules that have a DbContext, in catalogue order.
    /// </summary>
    public static IReadOnlyList<IModuleDatabase> Databases(IEnumerable<IModuleInstaller> installers)
    {
        ArgumentNullException.ThrowIfNull(installers);
        return [PlatformPersistence.Database, .. installers.Select(i => i.Database).OfType<IModuleDatabase>()];
    }

    /// <inheritdoc cref="Databases(IEnumerable{IModuleInstaller})"/>
    public static IReadOnlyList<IModuleDatabase> Databases() => Databases(CreateInstallers());
}
