using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 3 of STAGE_7_PLAN 1.6 drops the legacy access data only after Reporting copied it into the assignments (step 2);
/// a database several migrations behind, such as one at v0.6.0, would otherwise trip the drift guard.
/// </summary>
[MigrationDependsOn("reporting", "20261127154602_LegacyAccessToAssignments")]
internal partial class DropLegacyAccess;
