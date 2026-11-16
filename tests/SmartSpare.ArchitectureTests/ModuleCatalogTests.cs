using SmartSpare.Infrastructure.Persistence.Platform;

namespace SmartSpare.ArchitectureTests;

/// <summary>Order of modules and migrations (ADR 0011, MODULE_MAPPING section 5.2).</summary>
public sealed class ModuleCatalogTests
{
    [Fact]
    public void Catalog_ListsEveryModuleOnce_WithReportingLast()
    {
        var names = ModuleCatalog.ModuleCatalog.CreateInstallers().Select(i => i.GetType().Assembly.GetName().Name!).ToList();

        names.Order(StringComparer.Ordinal).ShouldBe(Solution.Modules.Select(m => m.Implementation.GetName().Name!).Order(StringComparer.Ordinal));
        names[^1].ShouldBe("SmartSpare.Modules.Reporting");
    }

    [Fact]
    public void Migrations_StartWithThePlatform() =>
        ModuleCatalog.ModuleCatalog.Databases()[0].ShouldBeSameAs(PlatformPersistence.Database);
}
