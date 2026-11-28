namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// A permission of the catalogue: its stable name, its stable numeric <paramref name="Code"/> (the bit it occupies in the
/// internal token, ADR 0028; assigned once, never reused or renumbered), module, resource, action and what it allows.
/// </summary>
public sealed record PermissionInfo(string Name, int Code, string Module, string Resource, string Action, string Description);

/// <summary>
/// The permission catalogue (ADR 0028, STAGE_7_PLAN 1.8): the only fixed part of authorization. Names are
/// <c>&lt;Module&gt;.&lt;Resource&gt;.&lt;Action&gt;</c> and never change, because roles (data) store them. Every name is also
/// a policy name: <see cref="PermissionPolicyProvider"/> builds "has this permission" on demand, so an endpoint says
/// <c>RequireAuthorization(Permissions.Inventory.Transfer.Approve)</c> and never names a role. A write needs the
/// resource's <c>View</c> as well where the endpoints sit in a group that requires it.
/// </summary>
public static class Permissions
{
    public static class Organisation
    {
        public static class Employee
        {
            public const string View = "Organisation.Employee.View";
            public const string Create = "Organisation.Employee.Create";
            public const string Edit = "Organisation.Employee.Edit";
            public const string Archive = "Organisation.Employee.Archive";
            public const string Anonymise = "Organisation.Employee.Anonymise";
        }

        public static class Position
        {
            public const string View = "Organisation.Position.View";
            public const string Create = "Organisation.Position.Create";
            public const string Edit = "Organisation.Position.Edit";
            public const string Archive = "Organisation.Position.Archive";
        }

        public static class Account
        {
            public const string View = "Organisation.Account.View";
            public const string Create = "Organisation.Account.Create";
            public const string Edit = "Organisation.Account.Edit";
        }

        public static class Role
        {
            public const string View = "Organisation.Role.View";
            public const string Edit = "Organisation.Role.Edit";
        }

        /// <summary>Hub scope (the record-rule layer, ADR 0026): who may write at every hub, not only the assigned ones.</summary>
        public static class Scope
        {
            public const string AllHubs = "Organisation.Scope.AllHubs";
        }
    }

    public static class Inventory
    {
        public static class Part
        {
            public const string View = "Inventory.Part.View";
            public const string Create = "Inventory.Part.Create";
            public const string Edit = "Inventory.Part.Edit";
            public const string Archive = "Inventory.Part.Archive";
        }

        public static class Brand
        {
            public const string View = "Inventory.Brand.View";
            public const string Create = "Inventory.Brand.Create";
            public const string Edit = "Inventory.Brand.Edit";
            public const string Archive = "Inventory.Brand.Archive";
            public const string ManageAssignments = "Inventory.Brand.ManageAssignments";
        }

        public static class Manufacturer
        {
            public const string View = "Inventory.Manufacturer.View";
            public const string Create = "Inventory.Manufacturer.Create";
            public const string Edit = "Inventory.Manufacturer.Edit";
            public const string Archive = "Inventory.Manufacturer.Archive";
        }

        public static class Warehouse
        {
            public const string View = "Inventory.Warehouse.View";
            public const string ManageAssignments = "Inventory.Warehouse.ManageAssignments";
        }

        public static class Stock
        {
            public const string View = "Inventory.Stock.View";
            public const string EditLevels = "Inventory.Stock.EditLevels";
            public const string Adjust = "Inventory.Stock.Adjust";
        }

        public static class Demand
        {
            public const string Flag = "Inventory.Demand.Flag";
        }

        public static class Transfer
        {
            public const string View = "Inventory.Transfer.View";
            public const string Create = "Inventory.Transfer.Create";
            public const string Approve = "Inventory.Transfer.Approve";
            public const string ApproveAnyBrand = "Inventory.Transfer.ApproveAnyBrand";
            public const string Cancel = "Inventory.Transfer.Cancel";
            public const string CancelAnyBrand = "Inventory.Transfer.CancelAnyBrand";
            public const string Dispatch = "Inventory.Transfer.Dispatch";
            public const string Receive = "Inventory.Transfer.Receive";
        }

        public static class Import
        {
            public const string View = "Inventory.Import.View";
            public const string Run = "Inventory.Import.Run";
        }

        public static class AdjustmentReason
        {
            public const string View = "Inventory.AdjustmentReason.View";
            public const string Create = "Inventory.AdjustmentReason.Create";
            public const string Edit = "Inventory.AdjustmentReason.Edit";
            public const string Archive = "Inventory.AdjustmentReason.Archive";
        }
    }

    public static class Fleet
    {
        public static class Machine
        {
            public const string View = "Fleet.Machine.View";
        }

        public static class EngineHours
        {
            public const string Record = "Fleet.EngineHours.Record";
        }

        public static class ServiceOrder
        {
            public const string View = "Fleet.ServiceOrder.View";
            public const string Create = "Fleet.ServiceOrder.Create";
            public const string Edit = "Fleet.ServiceOrder.Edit";
            public const string Cancel = "Fleet.ServiceOrder.Cancel";
            public const string Start = "Fleet.ServiceOrder.Start";
            public const string Complete = "Fleet.ServiceOrder.Complete";
        }
    }

    public static class Procurement
    {
        public static class PurchaseOrder
        {
            public const string Create = "Procurement.PurchaseOrder.Create";
            public const string Approve = "Procurement.PurchaseOrder.Approve";
            public const string ApproveAnyBrand = "Procurement.PurchaseOrder.ApproveAnyBrand";
            public const string Cancel = "Procurement.PurchaseOrder.Cancel";
            public const string CancelAnyBrand = "Procurement.PurchaseOrder.CancelAnyBrand";
            public const string Receive = "Procurement.PurchaseOrder.Receive";
        }

        public static class Supplier
        {
            public const string Edit = "Procurement.Supplier.Edit";
        }
    }

    public static class Forecasting
    {
        public static class Recommendation
        {
            public const string Decide = "Forecasting.Recommendation.Decide";
            public const string DecideAnyBrand = "Forecasting.Recommendation.DecideAnyBrand";
        }
    }

    public static class Audit
    {
        public static class Log
        {
            public const string View = "Audit.Log.View";
        }
    }

    /// <summary>Every permission with its description, in catalogue order (the UI groups them by module).</summary>
    public static IReadOnlyList<PermissionInfo> All { get; } =
    [
        P(Organisation.Employee.View, 1, "See employees and their positions."),
        P(Organisation.Employee.Create, 2, "Add employees."),
        P(Organisation.Employee.Edit, 3, "Change an employee's data and position."),
        P(Organisation.Employee.Archive, 4, "Archive or restore employees."),
        P(Organisation.Employee.Anonymise, 5, "Erase a former employee's personal data (GDPR)."),
        P(Organisation.Position.View, 6, "See positions and their role templates."),
        P(Organisation.Position.Create, 7, "Add positions."),
        P(Organisation.Position.Edit, 8, "Change a position and its role template."),
        P(Organisation.Position.Archive, 9, "Archive or restore positions."),
        P(Organisation.Account.View, 10, "See user accounts."),
        P(Organisation.Account.Create, 11, "Create an account for an employee."),
        P(Organisation.Account.Edit, 12, "Deactivate, reactivate, unlock accounts and reset passwords."),
        P(Organisation.Role.View, 13, "See roles and their permissions."),
        P(Organisation.Role.Edit, 14, "Create roles and change their permissions."),
        P(Organisation.Scope.AllHubs, 15, "Write at every hub, not only the hubs one is assigned to."),
        P(Inventory.Part.View, 16, "See the parts catalogue."),
        P(Inventory.Brand.View, 17, "See brands and who manages them."),
        P(Inventory.Brand.ManageAssignments, 18, "Assign brand managers and other brand responsibles."),
        P(Inventory.Warehouse.View, 19, "See warehouses and their responsibles."),
        P(Inventory.Warehouse.ManageAssignments, 20, "Assign keepers, service bases and other warehouse responsibles."),
        P(Inventory.Stock.View, 21, "See stock, positions and the ledger."),
        P(Inventory.Stock.EditLevels, 22, "Change safety and maximum stock."),
        P(Inventory.Stock.Adjust, 23, "Post stock count corrections."),
        P(Inventory.Demand.Flag, 24, "Mark demand as exceptional for forecasting."),
        P(Inventory.Transfer.View, 25, "See transfers."),
        P(Inventory.Transfer.Create, 26, "Create transfer drafts."),
        P(Inventory.Transfer.Approve, 27, "Approve transfers of the brands one manages."),
        P(Inventory.Transfer.ApproveAnyBrand, 28, "Approve transfers of every brand (with Approve)."),
        P(Inventory.Transfer.Cancel, 29, "Cancel transfers of the brands one manages."),
        P(Inventory.Transfer.CancelAnyBrand, 30, "Cancel transfers of every brand (with Cancel)."),
        P(Inventory.Transfer.Dispatch, 31, "Dispatch transfers from a hub."),
        P(Inventory.Transfer.Receive, 32, "Receive transfers at a hub."),
        P(Inventory.Import.View, 33, "See data imports and their errors."),
        P(Inventory.Import.Run, 34, "Import the parts catalogue and demand history."),
        P(Inventory.AdjustmentReason.View, 35, "See adjustment reasons."),
        P(Inventory.AdjustmentReason.Create, 36, "Add adjustment reasons."),
        P(Inventory.AdjustmentReason.Edit, 37, "Change adjustment reasons."),
        P(Inventory.AdjustmentReason.Archive, 38, "Archive or restore adjustment reasons."),
        P(Fleet.Machine.View, 39, "See customer machines, readings and due maintenance."),
        P(Fleet.EngineHours.Record, 40, "Log engine hours."),
        P(Fleet.ServiceOrder.View, 41, "See service orders."),
        P(Fleet.ServiceOrder.Create, 42, "Create service orders."),
        P(Fleet.ServiceOrder.Edit, 43, "Change the parts of a service order."),
        P(Fleet.ServiceOrder.Cancel, 44, "Cancel service orders."),
        P(Fleet.ServiceOrder.Start, 45, "Start service work."),
        P(Fleet.ServiceOrder.Complete, 46, "Complete service work."),
        P(Procurement.PurchaseOrder.Create, 47, "Create, submit and export purchase orders."),
        P(Procurement.PurchaseOrder.Approve, 48, "Approve purchase orders of the brands one manages, never one's own."),
        P(Procurement.PurchaseOrder.ApproveAnyBrand, 49, "Approve purchase orders of every brand (with Approve)."),
        P(Procurement.PurchaseOrder.Cancel, 50, "Cancel purchase orders of the brands one manages."),
        P(Procurement.PurchaseOrder.CancelAnyBrand, 51, "Cancel purchase orders of every brand (with Cancel)."),
        P(Procurement.PurchaseOrder.Receive, 52, "Receive purchase order lines into stock."),
        P(Forecasting.Recommendation.Decide, 53, "Accept or reject recommendations of the brands one manages."),
        P(Forecasting.Recommendation.DecideAnyBrand, 54, "Accept or reject recommendations of every brand (with Decide)."),
        P(Audit.Log.View, 55, "Read the audit trail."),
        P(Procurement.Supplier.Edit, 56, "Maintain suppliers, their parts and prices (never together with creating purchase orders)."),
        P(Inventory.Brand.Create, 57, "Add brands."),
        P(Inventory.Brand.Edit, 58, "Change brands."),
        P(Inventory.Brand.Archive, 59, "Archive or restore brands."),
        P(Inventory.Manufacturer.View, 60, "See part manufacturers."),
        P(Inventory.Manufacturer.Create, 61, "Add part manufacturers."),
        P(Inventory.Manufacturer.Edit, 62, "Change part manufacturers."),
        P(Inventory.Manufacturer.Archive, 63, "Archive or restore part manufacturers."),
        P(Inventory.Part.Create, 64, "Add parts to the catalogue."),
        P(Inventory.Part.Edit, 65, "Change parts of the catalogue."),
        P(Inventory.Part.Archive, 66, "Archive or restore parts."),
    ];

    /// <summary>Whether <paramref name="name"/> is a permission of the catalogue.</summary>
    public static bool Exists(string? name) => name is not null && ByName.ContainsKey(name);

    private static readonly Dictionary<string, PermissionInfo> ByName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);

    private static readonly Dictionary<int, PermissionInfo> ByCode = All.ToDictionary(p => p.Code);

    /// <summary>
    /// The highest code of the catalogue: the version of the bitset in the internal token (<see cref="PermissionBitset"/>).
    /// Codes only grow, so a higher version is a superset.
    /// </summary>
    public static int CatalogueVersion { get; } = All.Max(p => p.Code);

    public static PermissionInfo? Find(string name) => ByName.GetValueOrDefault(name);

    public static PermissionInfo? Find(int code) => ByCode.GetValueOrDefault(code);

    private static PermissionInfo P(string name, int code, string description)
    {
        var parts = name.Split('.');
        return new PermissionInfo(name, code, parts[0], parts[1], parts[2], description);
    }
}
