using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Parts;

/// <summary>
/// The Platform's part categories and units of measure (<c>ref</c>), read-only, for the part editor's choices (PR 7i1b).
/// They are seeded reference data, so they have no catalogue of their own.
/// </summary>
internal static class PartReferenceEndpoints
{
    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/part-categories", async (InventoryDbContext db, CancellationToken cancellationToken) =>
                Results.Ok(await db.Set<PartCategory>().AsNoTracking().OrderBy(c => c.Name)
                    .Select(c => new ReferenceItem(c.Id, c.Code, c.Name)).ToListAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(Permissions.Inventory.Part.View)
            .WithTags("Parts")
            .WithName("ListPartCategories")
            .WithSummary("Lists the part categories.")
            .WithDescription("Every part category of the reference data (ref.part_category), by name, for choosing a part's category. Permission Inventory.Part.View.")
            .Produces<IReadOnlyList<ReferenceItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { new ReferenceItem(11, "OIL_FILTER", "Engine oil filters") });

        v1.MapGet("/units-of-measure", async (InventoryDbContext db, CancellationToken cancellationToken) =>
                Results.Ok(await db.Set<UnitOfMeasure>().AsNoTracking().OrderBy(u => u.Code)
                    .Select(u => new ReferenceItem(u.Id, u.Code, u.Name)).ToListAsync(cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(Permissions.Inventory.Part.View)
            .WithTags("Parts")
            .WithName("ListUnitsOfMeasure")
            .WithSummary("Lists the units of measure.")
            .WithDescription("Every unit of measure of the reference data (ref.unit_of_measure), by code, for choosing a part's unit. Permission Inventory.Part.View.")
            .Produces<IReadOnlyList<ReferenceItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { new ReferenceItem(1, "PCS", "Pieces") });
    }
}
