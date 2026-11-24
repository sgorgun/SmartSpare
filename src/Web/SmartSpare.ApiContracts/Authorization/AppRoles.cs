namespace SmartSpare.ApiContracts.Authorization;

/// <summary>Application roles: the codes of <c>iam.role</c> (seeded by <c>db/sql/identity/seed_roles.sql</c>).</summary>
public static class AppRoles
{
    public const string Administrator = "ADMINISTRATOR";
    public const string BrandManager = "BRAND_MANAGER";
    public const string InventoryPlanner = "INVENTORY_PLANNER";
    public const string WarehouseKeeper = "WAREHOUSE_KEEPER";
    public const string ServiceEngineer = "SERVICE_ENGINEER";
    public const string Sponsor = "SPONSOR";

    public static IReadOnlyList<string> All { get; } =
        [Administrator, BrandManager, InventoryPlanner, WarehouseKeeper, ServiceEngineer, Sponsor];
}
