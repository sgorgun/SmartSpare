using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Features.PartCatalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;

namespace SmartSpare.Modules.Inventory.Features.Parts;

/// <summary>
/// The parts catalogue under <c>/api/v1/inventory/parts</c> on the shared master-data mechanism (STAGE_7_PLAN 3.1,
/// PR 7i1). The OEM number is the code, unique per brand; the description is the name. Every write publishes
/// <see cref="Contracts.PartCatalogChanged"/> in its transaction, so Fleet's part copy follows. An archived brand or
/// manufacturer cannot be chosen (SS500, refused by the database), and a part that still has stock, open transfers or
/// kit holds cannot be archived (SS501, also checked by the database for stock). <c>GET /parts/{id}</c> stays the part
/// with its stock per warehouse; the bulk import keeps its own CSV pipeline.
/// </summary>
internal static class PartCatalog
{
    private static readonly TransferStatus[] OpenTransfer = [TransferStatus.Proposed, TransferStatus.Approved, TransferStatus.Dispatched];

    public static CatalogDefinition<Part, long, PartDto, PartWrite> Definition { get; } = new()
    {
        Route = "/parts",
        ApiBase = "/api/v1/inventory",
        Tag = "Parts",
        Noun = "part",
        OperationNoun = "Part",
        ViewPermission = Permissions.Inventory.Part.View,
        CreatePermission = Permissions.Inventory.Part.Create,
        EditPermission = Permissions.Inventory.Part.Edit,
        ArchivePermission = Permissions.Inventory.Part.Archive,
        AuditTable = "catalog." + Infrastructure.Persistence.Configurations.PartConfiguration.Table,
        Projection = p => new PartDto(
            p.Id, p.Brand!.Code, p.Code, p.Name, p.Category!.Name, p.Unit!.Code, p.UnitCost, p.BrandId, p.CategoryId, p.Category.Code, p.UomId,
            p.WeightKg, p.IsConditionBased, p.ManufacturerId, p.Manufacturer != null ? p.Manufacturer.Code : null, p.IsArchived,
            Versions.Format(EF.Property<uint>(p, "xmin"))),
        VersionOf = p => p.Version,
        IdOf = p => p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Lookup = p => new CatalogLookupItem(p.Id, p.Code, p.Name, p.Brand!.Code),
        NormaliseCode = Part.NormaliseCode,
        CodeScope = part => other => other.BrandId == part.BrandId,
        MapDetail = false,
        Create = w => Part.Create(w.BrandId, w.Code, w.Name, w.CategoryId, w.UomId, w.WeightKg, w.UnitCost, w.IsConditionBased, w.ManufacturerId),
        Update = UpdateAsWritten,
        ArchiveGuard = ArchiveGuardAsync,
        Publish = PublishAsync,
        Validator = new PartWriteValidator(),
        FieldLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["oem_part_number"] = "OEM number",
            ["description"] = "Description",
            ["category_id"] = "Category",
            ["uom_id"] = "Unit",
            ["weight_kg"] = "Weight, kg",
            ["unit_cost"] = "Unit cost",
            ["is_condition_based"] = "Replaced as required",
            ["manufacturer_id"] = "Manufacturer",
        },
        Rules = "Permissions Inventory.Part.View to read and .Create, .Edit, .Archive to change (default roles: planners and administrators change). "
            + "The OEM number is unique per brand; brand and OEM number are fixed after creation. An archived brand or manufacturer cannot be chosen "
            + "(422 SS500). A part with stock, open transfers or kit holds cannot be archived (409 SS501). The bulk CSV import is POST /imports.",
        Example = new PartDto(1042, "JD", "RE504836", "Engine oil filter", "Engine oil filters", "PCS", 18.40m, 2, 11, "OIL_FILTER", 1, 0.450m, false, 1, "MANN", false, "9120"),
        WriteExample = new PartWrite("RE504836", "Engine oil filter", 2, 11, 1, 0.450m, 18.40m, false, 1),
        LookupExample = new CatalogLookupItem(1042, "RE504836", "Engine oil filter", "JD"),
    };

    public static void Map(IEndpointRouteBuilder v1) =>
        v1.MapCatalog<InventoryDbContext, Part, long, PartDto, PartWrite>(Definition);

    /// <summary>The brand is part of the key: a write that names another brand is refused, never applied.</summary>
    private static SmartSpare.SharedKernel.Result<bool> UpdateAsWritten(Part part, PartWrite write) =>
        write.BrandId != part.BrandId
            ? PartErrors.BrandFixed
            : part.Update(write.Name, write.CategoryId, write.UomId, write.WeightKg, write.UnitCost, write.IsConditionBased, write.ManufacturerId);

    /// <summary>Queues <see cref="Contracts.PartCatalogChanged"/> with the written parts as Fleet's copy reads them.</summary>
    private static async Task PublishAsync(
        IServiceProvider services, SmartSpare.Infrastructure.Persistence.IUnitOfWork unitOfWork, IReadOnlyList<Part> parts, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<InventoryDbContext>();
        var snapshots = await PartCatalogSnapshots.ByIdsAsync(db, [.. parts.Select(p => p.Id)], cancellationToken).ConfigureAwait(false);
        PartCatalogPublication.Queue(unitOfWork, snapshots);
    }

    /// <summary>SS501 reasons: stock on hand or reserved per hub, open transfers and kit holds of the part.</summary>
    private static async Task<IReadOnlyList<string>> ArchiveGuardAsync(IServiceProvider services, Part part, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<InventoryDbContext>();
        var stock = await (from b in db.Set<StockBalance>().AsNoTracking()
                           join w in db.Set<Warehouse>().AsNoTracking() on b.WarehouseId equals w.Id
                           where b.PartId == part.Id && (b.OnHandQty > 0 || b.ReservedQty > 0)
                           orderby w.Code
                           select new { w.Code, b.OnHandQty, b.ReservedQty })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var transfers = await db.Set<TransferOrder>().AsNoTracking()
            .CountAsync(t => OpenTransfer.Contains(t.Status) && t.Lines.Any(l => l.PartId == part.Id), cancellationToken).ConfigureAwait(false);
        var holds = await db.Set<StockReservation>().AsNoTracking()
            .CountAsync(r => r.PartId == part.Id && r.ServiceOrderLineId != null
                             && (r.Status == ReservationStatus.Soft || r.Status == ReservationStatus.Firm), cancellationToken).ConfigureAwait(false);

        var reasons = stock.Select(s => s.ReservedQty > 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"on hand {s.OnHandQty:0.###}, reserved {s.ReservedQty:0.###} at {s.Code}")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"on hand {s.OnHandQty:0.###} at {s.Code}"))
            .ToList();
        if (transfers > 0)
        {
            reasons.Add($"{transfers} open transfer(s)");
        }

        if (holds > 0)
        {
            reasons.Add($"{holds} kit hold(s) for service orders");
        }

        return reasons;
    }
}

/// <summary>Errors of the parts catalogue.</summary>
internal static class PartErrors
{
    public static readonly SmartSpare.SharedKernel.Error BrandFixed = SmartSpare.SharedKernel.Error.BusinessRule(
        "Inventory.PartBrandFixed", "The brand is part of the part's key and cannot be changed; create a new part instead.");
}
