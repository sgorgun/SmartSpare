using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations;

/// <summary>Reporting's data-quality view must stop reading <c>catalog.part.is_active</c> before the column goes (PR 7i1).</summary>
[MigrationDependsOn("reporting", "20261128130423_PartArchiveInDataQuality")]
internal partial class DropPartIsActive;
