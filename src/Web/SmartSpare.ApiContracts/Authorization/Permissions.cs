namespace SmartSpare.ApiContracts.Authorization;

/// <summary>A permission of the catalogue: its stable name, module, resource, action and what it allows.</summary>
public sealed record PermissionInfo(string Name, string Module, string Resource, string Action, string Description);

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
        }

        public static class Brand
        {
            public const string View = "Inventory.Brand.View";
            public const string ManageAssignments = "Inventory.Brand.ManageAssignments";
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
        P(Organisation.Employee.View, "See employees and their positions."),
        P(Organisation.Employee.Create, "Add employees."),
        P(Organisation.Employee.Edit, "Change an employee's data and position."),
        P(Organisation.Employee.Archive, "Archive or restore employees."),
        P(Organisation.Employee.Anonymise, "Erase a former employee's personal data (GDPR)."),
        P(Organisation.Position.View, "See positions and their role templates."),
        P(Organisation.Position.Create, "Add positions."),
        P(Organisation.Position.Edit, "Change a position and its role template."),
        P(Organisation.Position.Archive, "Archive or restore positions."),
        P(Organisation.Account.View, "See user accounts."),
        P(Organisation.Account.Create, "Create an account for an employee."),
        P(Organisation.Account.Edit, "Deactivate, reactivate, unlock accounts and reset passwords."),
        P(Organisation.Role.View, "See roles and their permissions."),
        P(Organisation.Role.Edit, "Create roles and change their permissions."),
        P(Organisation.Scope.AllHubs, "Write at every hub, not only the hubs one is assigned to."),
        P(Inventory.Part.View, "See the parts catalogue."),
        P(Inventory.Brand.View, "See brands and who manages them."),
        P(Inventory.Brand.ManageAssignments, "Assign brand managers and other brand responsibles."),
        P(Inventory.Warehouse.View, "See warehouses and their responsibles."),
        P(Inventory.Warehouse.ManageAssignments, "Assign keepers, service bases and other warehouse responsibles."),
        P(Inventory.Stock.View, "See stock, positions and the ledger."),
        P(Inventory.Stock.EditLevels, "Change safety and maximum stock."),
        P(Inventory.Stock.Adjust, "Post stock count corrections."),
        P(Inventory.Demand.Flag, "Mark demand as exceptional for forecasting."),
        P(Inventory.Transfer.View, "See transfers."),
        P(Inventory.Transfer.Create, "Create transfer drafts."),
        P(Inventory.Transfer.Approve, "Approve transfers of the brands one manages."),
        P(Inventory.Transfer.ApproveAnyBrand, "Approve transfers of every brand (with Approve)."),
        P(Inventory.Transfer.Cancel, "Cancel transfers of the brands one manages."),
        P(Inventory.Transfer.CancelAnyBrand, "Cancel transfers of every brand (with Cancel)."),
        P(Inventory.Transfer.Dispatch, "Dispatch transfers from a hub."),
        P(Inventory.Transfer.Receive, "Receive transfers at a hub."),
        P(Inventory.Import.View, "See data imports and their errors."),
        P(Inventory.Import.Run, "Import the parts catalogue and demand history."),
        P(Inventory.AdjustmentReason.View, "See adjustment reasons."),
        P(Inventory.AdjustmentReason.Create, "Add adjustment reasons."),
        P(Inventory.AdjustmentReason.Edit, "Change adjustment reasons."),
        P(Inventory.AdjustmentReason.Archive, "Archive or restore adjustment reasons."),
        P(Fleet.Machine.View, "See customer machines, readings and due maintenance."),
        P(Fleet.EngineHours.Record, "Log engine hours."),
        P(Fleet.ServiceOrder.View, "See service orders."),
        P(Fleet.ServiceOrder.Create, "Create service orders."),
        P(Fleet.ServiceOrder.Edit, "Change the parts of a service order."),
        P(Fleet.ServiceOrder.Cancel, "Cancel service orders."),
        P(Fleet.ServiceOrder.Start, "Start service work."),
        P(Fleet.ServiceOrder.Complete, "Complete service work."),
        P(Procurement.PurchaseOrder.Create, "Create, submit and export purchase orders."),
        P(Procurement.PurchaseOrder.Approve, "Approve purchase orders of the brands one manages, never one's own."),
        P(Procurement.PurchaseOrder.ApproveAnyBrand, "Approve purchase orders of every brand (with Approve)."),
        P(Procurement.PurchaseOrder.Cancel, "Cancel purchase orders of the brands one manages."),
        P(Procurement.PurchaseOrder.CancelAnyBrand, "Cancel purchase orders of every brand (with Cancel)."),
        P(Procurement.PurchaseOrder.Receive, "Receive purchase order lines into stock."),
        P(Forecasting.Recommendation.Decide, "Accept or reject recommendations of the brands one manages."),
        P(Forecasting.Recommendation.DecideAnyBrand, "Accept or reject recommendations of every brand (with Decide)."),
        P(Audit.Log.View, "Read the audit trail."),
    ];

    private static readonly HashSet<string> Names = [.. All.Select(p => p.Name)];

    /// <summary>Whether <paramref name="name"/> is a permission of the catalogue.</summary>
    public static bool Exists(string? name) => name is not null && Names.Contains(name);

    private static PermissionInfo P(string name, string description)
    {
        var parts = name.Split('.');
        return new PermissionInfo(name, parts[0], parts[1], parts[2], description);
    }
}
