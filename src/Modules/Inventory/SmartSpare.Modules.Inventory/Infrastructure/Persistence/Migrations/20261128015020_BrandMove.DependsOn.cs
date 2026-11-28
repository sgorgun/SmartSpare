using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Migrations;

/// <summary>
/// The brand move (STAGE_7_PLAN 3.2) runs only after the older migrations that name <c>ref.brand</c> in their SQL: Fleet's
/// foreign key and machine card view, Reporting's transitional views. Matters on a fresh database.
/// </summary>
[MigrationDependsOn("fleet", "20261116222230_InitialFleet")]
[MigrationDependsOn("reporting", "20261116230254_TransitionalCrossModuleObjects")]
internal partial class BrandMove;
