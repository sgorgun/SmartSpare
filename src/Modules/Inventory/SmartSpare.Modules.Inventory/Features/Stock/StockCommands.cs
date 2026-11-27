using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Stock;

internal static class StockErrors
{
    public static readonly Error VersionRequired = Error.Validation(
        "Inventory.VersionRequired", "Send the version of the stock levels you changed as If-Match (the ETag of the position).");
}

/// <summary>Changes the safety and maximum stock of a part in a hub, if nobody changed them since <see cref="Version"/>.</summary>
internal sealed record ChangeStockLevelsCommand(short WarehouseId, long PartId, decimal SafetyStock, decimal? MaxStock, uint? Version)
    : ICommand<StockRow>;

internal sealed class ChangeStockLevelsCommandValidator : AbstractValidator<ChangeStockLevelsCommand>
{
    public ChangeStockLevelsCommandValidator()
    {
        RuleFor(c => new ChangeStockLevelsRequest(c.SafetyStock, c.MaxStock)).SetValidator(new ChangeStockLevelsRequestValidator())
            .OverridePropertyName(string.Empty);
    }
}

internal sealed class ChangeStockLevelsCommandHandler(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork)
    : ICommandHandler<ChangeStockLevelsCommand, StockRow>
{
    public async Task<Result<StockRow>> Handle(ChangeStockLevelsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Version is not { } version)
        {
            return StockErrors.VersionRequired;
        }

        var policy = await db.Set<WarehousePart>()
            .SingleOrDefaultAsync(p => p.WarehouseId == command.WarehouseId && p.PartId == command.PartId, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return InventoryErrors.StockPositionNotFound;
        }

        // The version the client saw: a change since then fails the UPDATE (DbUpdateConcurrencyException → 409).
        db.Entry(policy).Property<uint>("xmin").OriginalValue = version;
        var changed = policy.ChangeStockLevels(command.SafetyStock, command.MaxStock);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await StockRows.FindAsync(db, command.WarehouseId, command.PartId, cancellationToken).ConfigureAwait(false))!;
    }
}

internal static class ChangeStockLevelsEndpoint
{
    /// <summary>PUT /api/v1/inventory/stock/{warehouseId}/{partId}/levels — change safety and maximum stock.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPut("/stock/{warehouseId:int}/{partId:long}/levels", async (
                [Description("Hub id.")] short warehouseId,
                [Description("Part id.")] long partId,
                ChangeStockLevelsRequest request,
                [FromHeader(Name = "If-Match")][Description("The ETag of the position as read, for example \"7731\".")] string? ifMatch,
                ICommandHandler<ChangeStockLevelsCommand, StockRow> handler,
                CancellationToken cancellationToken) =>
            {
                var command = new ChangeStockLevelsCommand(warehouseId, partId, request.SafetyStock, request.MaxStock, Versions.Parse(ifMatch));
                var result = await handler.Handle(command, cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AppPolicies.PlanInventory)
            .WithName("ChangeStockLevels")
            .WithSummary("Changes the safety and maximum stock of a part in a hub.")
            .WithDescription(
                "Sets the safety stock (zero or more) and the maximum stock (empty, or not below the safety stock). The "
                + "If-Match header must carry the ETag returned with the position; if someone changed the levels since, the "
                + "answer is 409 with code Database.ConcurrencyConflict and nothing is saved. Returns the updated overview "
                + "row. Policy PlanInventory (brand managers and inventory planners).")
            .Accepts<ChangeStockLevelsRequest>("application/json")
            .Produces<StockRow>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new ChangeStockLevelsRequest(SafetyStock: 6, MaxStock: 30))
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.StockRow)
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                "MaxStock", "'Max Stock' must not be below the safety stock."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.StockPositionNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(Error.Conflict(
                "Database.ConcurrencyConflict", "The record was changed by someone else. Reload it and try again.")));
}

/// <summary>Posts a stock count correction (ADJUSTMENT_IN or ADJUSTMENT_OUT) in a hub.</summary>
internal sealed record AdjustStockCommand(short WarehouseId, long PartId, decimal Quantity, string Note) : ICommand<StockAdjustmentResponse>;

internal sealed class AdjustStockCommandValidator : AbstractValidator<AdjustStockCommand>
{
    public AdjustStockCommandValidator()
    {
        RuleFor(c => new StockAdjustmentRequest(c.WarehouseId, c.PartId, c.Quantity, c.Note)).SetValidator(new StockAdjustmentRequestValidator())
            .OverridePropertyName(string.Empty);
    }
}

internal sealed class AdjustStockCommandHandler(
    InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<AdjustStockCommand, StockAdjustmentResponse>
{
    public async Task<Result<StockAdjustmentResponse>> Handle(AdjustStockCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A keeper corrects the stock of the own hub only (the rule of SS302 for dispatch and receipt).
        if (!HubAccess.MayWrite(user, command.WarehouseId))
        {
            return InventoryErrors.NotYourWarehouse;
        }

        var stocked = await db.Set<WarehousePart>().AsNoTracking()
            .AnyAsync(p => p.WarehouseId == command.WarehouseId && p.PartId == command.PartId, cancellationToken).ConfigureAwait(false);
        if (!stocked)
        {
            return InventoryErrors.StockPositionNotFound;
        }

        var unitCost = await db.Set<Part>().AsNoTracking().Where(p => p.Id == command.PartId).Select(p => p.UnitCost)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var movement = StockMovement.Adjust(
            command.WarehouseId, command.PartId, command.Quantity, unitCost, command.Note,
            user.UserId ?? throw new InvalidOperationException("A stock adjustment needs a signed-in user."), clock.GetUtcNow());
        if (movement.IsFailure)
        {
            return movement.Error;
        }

        // The ledger trigger applies the quantity and refuses a shortage larger than the available stock (SS200 → 409).
        db.Add(movement.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var stock = await StockRows.FindAsync(db, command.WarehouseId, command.PartId, cancellationToken).ConfigureAwait(false);
        return new StockAdjustmentResponse(movement.Value.Id, stock!);
    }
}

internal static class AdjustStockEndpoint
{
    /// <summary>POST /api/v1/inventory/stock-adjustments — post a stock count correction.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPost("/stock-adjustments", async (
                StockAdjustmentRequest request,
                ICommandHandler<AdjustStockCommand, StockAdjustmentResponse> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(
                    new AdjustStockCommand(request.WarehouseId, request.PartId, request.Quantity, request.Note), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
                    : result.Error.ToProblem();
            })
            .RequireAuthorization(AppPolicies.MoveStock)
            .WithName("AdjustStock")
            .WithSummary("Posts a stock count correction in a hub.")
            .WithDescription(
                "A positive quantity posts ADJUSTMENT_IN (surplus), a negative one ADJUSTMENT_OUT (shortage, scrap); a note is "
                + "required. A keeper may correct only the own hub (403, code SS302). A shortage larger than the available "
                + "stock is refused by the ledger with 409, code SS200. Returns the movement id and the position after it. "
                + "Policy MoveStock (warehouse keepers and administrators).")
            .Accepts<StockAdjustmentRequest>("application/json")
            .Produces<StockAdjustmentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new StockAdjustmentRequest(WarehouseId: 3, PartId: 1042, Quantity: -2, Note: "Damaged in storage, scrapped"))
            .WithResponseExample(StatusCodes.Status201Created, new StockAdjustmentResponse(884213, InventoryExamples.StockRow))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem("Note", "'Note' must not be empty."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, InventoryExamples.Problem(InventoryErrors.NotYourWarehouse))
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.StockPositionNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(Error.Conflict(
                "SS200", "Insufficient stock of part 1042 in warehouse 3: available 1.000, requested 2.000 (ADJUSTMENT_OUT)")));
}
