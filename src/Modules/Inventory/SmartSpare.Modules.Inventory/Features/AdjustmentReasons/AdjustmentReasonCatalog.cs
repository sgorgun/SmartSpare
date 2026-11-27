using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.AdjustmentReasons;

/// <summary>
/// The adjustment reasons catalogue under <c>/api/v1/inventory/adjustment-reasons</c> (STAGE_7_PLAN 3.1), built by the
/// shared master-data mechanism (ADR 0025). Every role reads it (the stock screen offers the reasons); planners and
/// administrators maintain it. Nothing blocks archiving: past adjustments keep their reason.
/// </summary>
internal static class AdjustmentReasonCatalog
{
    public static CatalogDefinition<AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite> Definition { get; } = new()
    {
        Route = "/adjustment-reasons",
        ApiBase = "/api/v1/inventory",
        Tag = "Adjustment reasons",
        Noun = "adjustment reason",
        OperationNoun = "AdjustmentReason",
        ReadPolicy = AppPolicies.ReadInventory,
        WritePolicy = AppPolicies.ManageCatalog,
        AuditTable = "inventory." + Infrastructure.Persistence.Configurations.AdjustmentReasonConfiguration.Table,
        Projection = r => new AdjustmentReasonDto(
            r.Id, r.Code, r.Name, r.Direction, r.RequiresNote, r.IsArchived, Versions.Format(EF.Property<uint>(r, "xmin"))),
        VersionOf = r => r.Version,
        IdOf = r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Lookup = r => new CatalogLookupItem(r.Id, r.Code, r.Name, r.Direction),
        NormaliseCode = AdjustmentReason.NormaliseCode,
        Create = w => AdjustmentReason.Create(w.Code, w.Name, w.Direction, w.RequiresNote),
        Update = (r, w) => r.Update(w.Name, w.Direction, w.RequiresNote),
        Validator = new AdjustmentReasonWriteValidator(),
        FieldLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = "Code",
            ["name"] = "Name",
            ["direction"] = "Direction",
            ["requires_note"] = "Note required",
        },
        Rules = "Policy ReadInventory to read, ManageCatalog (planners, administrators) to change.",
        Example = new AdjustmentReasonDto(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out, false, false, "8841"),
        WriteExample = new AdjustmentReasonWrite("DAMAGED", "Damaged in storage", AdjustmentDirections.Out, false),
        LookupExample = new CatalogLookupItem(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out),
    };

    public static void Map(IEndpointRouteBuilder v1) =>
        v1.MapCatalog<InventoryDbContext, AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite>(Definition);
}
