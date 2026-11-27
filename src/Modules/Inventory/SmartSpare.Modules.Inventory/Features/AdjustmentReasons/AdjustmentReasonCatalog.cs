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
/// shared master-data mechanism (ADR 0025). Every default role reads it (the stock screen offers the reasons); the
/// default planner and administrator roles maintain it. Nothing blocks archiving: past adjustments keep their reason.
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
        ViewPermission = Permissions.Inventory.AdjustmentReason.View,
        CreatePermission = Permissions.Inventory.AdjustmentReason.Create,
        EditPermission = Permissions.Inventory.AdjustmentReason.Edit,
        ArchivePermission = Permissions.Inventory.AdjustmentReason.Archive,
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
        Rules = "Permissions Inventory.AdjustmentReason.View to read and .Create, .Edit, .Archive to change (default roles: planners and administrators change).",
        Example = new AdjustmentReasonDto(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out, false, false, "8841"),
        WriteExample = new AdjustmentReasonWrite("DAMAGED", "Damaged in storage", AdjustmentDirections.Out, false),
        LookupExample = new CatalogLookupItem(2, "DAMAGED", "Damaged in storage", AdjustmentDirections.Out),
    };

    public static void Map(IEndpointRouteBuilder v1) =>
        v1.MapCatalog<InventoryDbContext, AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite>(Definition);
}
