namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// The permissions of the seeded roles (ADR 0028). They reproduce exactly the rights the role policies gave before
/// stage 7 PR 7x1 (the mapping table is in STAGE_7_PLAN 1.8 and <c>DefaultRolesTests</c> proves it). The database seed
/// (<c>db/sql/identity/0011_permissions.sql</c>) holds the same bundles; a test fails if the two differ. Roles are data:
/// administrators change them later, so code reads the database, not this table, except to seed and to test.
/// </summary>
public static class DefaultRoles
{
    private static readonly string[] ReadEverything =
    [
        Permissions.Inventory.Part.View, Permissions.Inventory.Brand.View, Permissions.Inventory.Warehouse.View,
        Permissions.Inventory.Stock.View, Permissions.Inventory.Transfer.View, Permissions.Inventory.AdjustmentReason.View,
        Permissions.Fleet.Machine.View, Permissions.Fleet.ServiceOrder.View,
    ];

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Bundles { get; } = new Dictionary<string, IReadOnlySet<string>>
    {
        [AppRoles.Administrator] = Set(
        [
            Permissions.Organisation.Employee.View, Permissions.Organisation.Employee.Create,
            Permissions.Organisation.Employee.Edit, Permissions.Organisation.Employee.Archive,
            Permissions.Organisation.Employee.Anonymise,
            Permissions.Organisation.Position.View, Permissions.Organisation.Position.Create,
            Permissions.Organisation.Position.Edit, Permissions.Organisation.Position.Archive,
            Permissions.Organisation.Account.View, Permissions.Organisation.Account.Create,
            Permissions.Organisation.Account.Edit,
            Permissions.Organisation.Role.View, Permissions.Organisation.Role.Edit,
            Permissions.Organisation.Scope.AllHubs,
            Permissions.Inventory.Transfer.CancelAnyBrand, Permissions.Procurement.PurchaseOrder.CancelAnyBrand,
            Permissions.Inventory.Brand.ManageAssignments, Permissions.Inventory.Warehouse.ManageAssignments,
            Permissions.Inventory.Transfer.Cancel,
            Permissions.Inventory.Stock.Adjust, Permissions.Inventory.Transfer.Dispatch,
            Permissions.Inventory.Transfer.Receive, Permissions.Procurement.PurchaseOrder.Receive,
            Permissions.Fleet.EngineHours.Record, Permissions.Fleet.ServiceOrder.Start,
            Permissions.Fleet.ServiceOrder.Complete,
            Permissions.Fleet.ServiceOrder.Create, Permissions.Fleet.ServiceOrder.Edit,
            Permissions.Fleet.ServiceOrder.Cancel,
            Permissions.Procurement.PurchaseOrder.Cancel,
            Permissions.Audit.Log.View,
            Permissions.Inventory.Import.View, Permissions.Inventory.Import.Run,
            Permissions.Inventory.AdjustmentReason.Create, Permissions.Inventory.AdjustmentReason.Edit,
            Permissions.Inventory.AdjustmentReason.Archive,
        ]),
        [AppRoles.BrandManager] = Set(
        [
            Permissions.Inventory.Stock.EditLevels, Permissions.Inventory.Demand.Flag,
            Permissions.Inventory.Transfer.Create, Permissions.Procurement.PurchaseOrder.Create,
            Permissions.Inventory.Transfer.Approve, Permissions.Inventory.Transfer.Cancel,
            Permissions.Fleet.ServiceOrder.Create, Permissions.Fleet.ServiceOrder.Edit,
            Permissions.Fleet.ServiceOrder.Cancel,
            Permissions.Procurement.PurchaseOrder.Approve, Permissions.Procurement.PurchaseOrder.Cancel,
            Permissions.Forecasting.Recommendation.Decide,
        ]),
        [AppRoles.InventoryPlanner] = Set(
        [
            Permissions.Inventory.Transfer.ApproveAnyBrand, Permissions.Inventory.Transfer.CancelAnyBrand,
            Permissions.Forecasting.Recommendation.DecideAnyBrand,
            Permissions.Inventory.Stock.EditLevels, Permissions.Inventory.Demand.Flag,
            Permissions.Inventory.Transfer.Create, Permissions.Procurement.PurchaseOrder.Create,
            Permissions.Inventory.Transfer.Approve, Permissions.Inventory.Transfer.Cancel,
            Permissions.Forecasting.Recommendation.Decide,
            Permissions.Inventory.Import.View, Permissions.Inventory.Import.Run,
            Permissions.Inventory.AdjustmentReason.Create, Permissions.Inventory.AdjustmentReason.Edit,
            Permissions.Inventory.AdjustmentReason.Archive,
        ]),
        [AppRoles.WarehouseKeeper] = Set(
        [
            Permissions.Inventory.Stock.Adjust, Permissions.Inventory.Transfer.Dispatch,
            Permissions.Inventory.Transfer.Receive, Permissions.Procurement.PurchaseOrder.Receive,
        ]),
        [AppRoles.ServiceEngineer] = Set(
        [
            Permissions.Fleet.EngineHours.Record, Permissions.Fleet.ServiceOrder.Start,
            Permissions.Fleet.ServiceOrder.Complete,
            Permissions.Fleet.ServiceOrder.Create, Permissions.Fleet.ServiceOrder.Edit,
            Permissions.Fleet.ServiceOrder.Cancel,
        ]),
        [AppRoles.Sponsor] = Set(
        [
            Permissions.Procurement.PurchaseOrder.ApproveAnyBrand, Permissions.Procurement.PurchaseOrder.CancelAnyBrand,
            Permissions.Procurement.PurchaseOrder.Approve, Permissions.Procurement.PurchaseOrder.Cancel,
            Permissions.Audit.Log.View,
        ]),
    };

    /// <summary>The union of the default permissions of <paramref name="roles"/> (for tests and the seed check).</summary>
    public static IReadOnlySet<string> Of(IEnumerable<string> roles) =>
        roles.Where(Bundles.ContainsKey).SelectMany(r => Bundles[r]).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Set(string[] permissions) => [.. permissions.Concat(ReadEverything)];
}
