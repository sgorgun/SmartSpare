using System.Buffers.Text;
using SmartSpare.ApiContracts.Authorization;

namespace SmartSpare.Infrastructure;

/// <summary>
/// The compact permission set of the internal token (ADR 0028): stable codes and the bitset. The codes below are pinned:
/// a code is assigned once and never renumbered or reused, because services with an older catalogue decode by code.
/// A new permission gets the next code and is appended here.
/// </summary>
public sealed class PermissionBitsetTests
{
    private static readonly Dictionary<int, string> PinnedCodes = new()
    {
        [1] = "Organisation.Employee.View",
        [2] = "Organisation.Employee.Create",
        [3] = "Organisation.Employee.Edit",
        [4] = "Organisation.Employee.Archive",
        [5] = "Organisation.Employee.Anonymise",
        [6] = "Organisation.Position.View",
        [7] = "Organisation.Position.Create",
        [8] = "Organisation.Position.Edit",
        [9] = "Organisation.Position.Archive",
        [10] = "Organisation.Account.View",
        [11] = "Organisation.Account.Create",
        [12] = "Organisation.Account.Edit",
        [13] = "Organisation.Role.View",
        [14] = "Organisation.Role.Edit",
        [15] = "Organisation.Scope.AllHubs",
        [16] = "Inventory.Part.View",
        [17] = "Inventory.Brand.View",
        [18] = "Inventory.Brand.ManageAssignments",
        [19] = "Inventory.Warehouse.View",
        [20] = "Inventory.Warehouse.ManageAssignments",
        [21] = "Inventory.Stock.View",
        [22] = "Inventory.Stock.EditLevels",
        [23] = "Inventory.Stock.Adjust",
        [24] = "Inventory.Demand.Flag",
        [25] = "Inventory.Transfer.View",
        [26] = "Inventory.Transfer.Create",
        [27] = "Inventory.Transfer.Approve",
        [28] = "Inventory.Transfer.ApproveAnyBrand",
        [29] = "Inventory.Transfer.Cancel",
        [30] = "Inventory.Transfer.CancelAnyBrand",
        [31] = "Inventory.Transfer.Dispatch",
        [32] = "Inventory.Transfer.Receive",
        [33] = "Inventory.Import.View",
        [34] = "Inventory.Import.Run",
        [35] = "Inventory.AdjustmentReason.View",
        [36] = "Inventory.AdjustmentReason.Create",
        [37] = "Inventory.AdjustmentReason.Edit",
        [38] = "Inventory.AdjustmentReason.Archive",
        [39] = "Fleet.Machine.View",
        [40] = "Fleet.EngineHours.Record",
        [41] = "Fleet.ServiceOrder.View",
        [42] = "Fleet.ServiceOrder.Create",
        [43] = "Fleet.ServiceOrder.Edit",
        [44] = "Fleet.ServiceOrder.Cancel",
        [45] = "Fleet.ServiceOrder.Start",
        [46] = "Fleet.ServiceOrder.Complete",
        [47] = "Procurement.PurchaseOrder.Create",
        [48] = "Procurement.PurchaseOrder.Approve",
        [49] = "Procurement.PurchaseOrder.ApproveAnyBrand",
        [50] = "Procurement.PurchaseOrder.Cancel",
        [51] = "Procurement.PurchaseOrder.CancelAnyBrand",
        [52] = "Procurement.PurchaseOrder.Receive",
        [53] = "Forecasting.Recommendation.Decide",
        [54] = "Forecasting.Recommendation.DecideAnyBrand",
        [55] = "Audit.Log.View",
        [56] = "Procurement.Supplier.Edit",
        [57] = "Inventory.Brand.Create",
        [58] = "Inventory.Brand.Edit",
        [59] = "Inventory.Brand.Archive",
        [60] = "Inventory.Manufacturer.View",
        [61] = "Inventory.Manufacturer.Create",
        [62] = "Inventory.Manufacturer.Edit",
        [63] = "Inventory.Manufacturer.Archive",
    };

    [Fact]
    public void Every_code_is_pinned_and_none_was_renumbered() =>
        Permissions.All.Select(p => (p.Code, p.Name)).Order().ShouldBe(PinnedCodes.Select(p => (p.Key, p.Value)).Order(),
            "append a new permission with the next code; never change or reuse an existing one");

    [Fact]
    public void The_catalogue_version_is_the_highest_code() =>
        Permissions.CatalogueVersion.ShouldBe(PinnedCodes.Keys.Max());

    [Fact]
    public void A_set_round_trips_through_the_bitset()
    {
        string[] permissions = [Permissions.Inventory.Transfer.Approve, Permissions.Fleet.Machine.View, Permissions.Organisation.Scope.AllHubs];

        PermissionBitset.Decode(PermissionBitset.Encode(permissions)).ShouldBe(permissions, ignoreOrder: true);
        PermissionBitset.Decode(PermissionBitset.Encode(Permissions.All.Select(p => p.Name))).Count.ShouldBe(Permissions.All.Count);
        PermissionBitset.Encode([]).ShouldBeEmpty();
        PermissionBitset.Decode(null).ShouldBeEmpty();
    }

    [Fact]
    public void Bits_of_codes_a_service_does_not_know_are_ignored()
    {
        // A token from a gateway with a newer catalogue: code 400 is beyond this one.
        var known = Permissions.Find(Permissions.Fleet.Machine.View)!.Code;
        var bytes = new byte[51];
        bytes[known / 8] |= (byte)(1 << (known % 8));
        bytes[400 / 8] |= 1 << (400 % 8);

        PermissionBitset.Decode(Base64Url.EncodeToString(bytes)).ShouldBe([Permissions.Fleet.Machine.View]);
    }

    [Theory]
    [InlineData(150, 26)]
    [InlineData(300, 51)]
    public void The_bitset_stays_tiny_as_the_catalogue_grows(int codes, int maxChars) =>
        Base64Url.EncodeToString(new byte[(codes / 8) + 1]).Length.ShouldBeLessThanOrEqualTo(maxChars);
}
