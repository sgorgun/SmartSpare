using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence.Migrations;

/// <summary>The view reads <c>catalog.part.is_archived</c>, which Inventory's <c>PartsAsCatalogue</c> adds (PR 7i1).</summary>
[MigrationDependsOn("inventory", "20261128130240_PartsAsCatalogue")]
internal partial class PartArchiveInDataQuality;
