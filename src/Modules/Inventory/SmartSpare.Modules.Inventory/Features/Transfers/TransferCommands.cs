using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Transfers;

internal static class TransferErrors
{
    public static readonly Error NoRoute = Error.BusinessRule(
        "Inventory.NoRoute", "There is no transport route between these hubs.");

    public static readonly Error UnknownPart = Error.BusinessRule(
        "Inventory.UnknownPart", "Every line needs an active part of the catalogue.");
}

/// <summary>Proposes a transfer between two hubs (written through the aggregate; triggers guard the tables).</summary>
internal sealed record CreateTransferCommand(CreateTransferRequest Request) : ICommand<TransferDetails>;

internal sealed class CreateTransferCommandValidator : AbstractValidator<CreateTransferCommand>
{
    public CreateTransferCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateTransferRequestValidator());
}

internal sealed class CreateTransferCommandHandler(
    InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, TransferDetailsReader reader)
    : ICommandHandler<CreateTransferCommand, TransferDetails>
{
    public async Task<Result<TransferDetails>> Handle(CreateTransferCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;

        var routeExists = await db.Set<WarehouseRoute>().AsNoTracking()
            .AnyAsync(r => r.FromWarehouseId == request.FromWarehouseId && r.ToWarehouseId == request.ToWarehouseId, cancellationToken)
            .ConfigureAwait(false);
        if (!routeExists)
        {
            return TransferErrors.NoRoute;
        }

        var partIds = request.Lines.Select(l => l.PartId).ToArray();
        var activeParts = await db.Set<Domain.Catalog.Part>().AsNoTracking().CountAsync(p => partIds.Contains(p.Id) && !p.IsArchived, cancellationToken)
            .ConfigureAwait(false);
        if (activeParts != partIds.Length)
        {
            return TransferErrors.UnknownPart;
        }

        var order = TransferOrder.Propose(
            request.FromWarehouseId, request.ToWarehouseId, request.IsUrgent,
            user.UserId ?? throw new InvalidOperationException("A transfer needs a signed-in user."),
            [.. request.Lines.Select(l => new TransferLineDraft(l.PartId, l.Quantity))]);
        if (order.IsFailure)
        {
            return order.Error;
        }

        db.Add(order.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await reader.ReadAsync(order.Value.Id, cancellationToken).ConfigureAwait(false))!;
    }
}

/// <summary>A state transition of a transfer order completed by its SQL function.</summary>
internal sealed record ApproveTransferCommand(long Id) : ICommand<TransferDetails>;

internal sealed record CancelTransferCommand(long Id, string Reason) : ICommand<TransferDetails>;

internal sealed record DispatchTransferCommand(long Id) : ICommand<TransferDetails>;

internal sealed record ReceiveTransferCommand(long Id) : ICommand<TransferDetails>;

internal sealed class CancelTransferCommandValidator : AbstractValidator<CancelTransferCommand>
{
    public CancelTransferCommandValidator() =>
        RuleFor(c => new CancelTransferRequest(c.Reason)).SetValidator(new CancelTransferRequestValidator()).OverridePropertyName(string.Empty);
}

/// <summary>
/// The pattern of STAGE_5_PLAN section 2.1 for approve, cancel, dispatch and receive: load the order untracked, check the
/// policy and brand scope, ask the aggregate, then call the SQL function in the same transaction (locks, reservations,
/// ledger, the role and warehouse checks again) in the same transaction.
/// </summary>
internal sealed class TransferTransitionHandler(
    TransferFunctions functions, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, TimeProvider clock,
    TransferAccess access, TransferDetailsReader reader)
    : ICommandHandler<ApproveTransferCommand, TransferDetails>,
      ICommandHandler<CancelTransferCommand, TransferDetails>,
      ICommandHandler<DispatchTransferCommand, TransferDetails>,
      ICommandHandler<ReceiveTransferCommand, TransferDetails>
{
    public Task<Result<TransferDetails>> Handle(ApproveTransferCommand command, CancellationToken cancellationToken) =>
        TransitionAsync(command.Id, TransferActions.Approve, async (order, now, ct) =>
        {
            var step = order.Approve(CurrentUserId(), now);
            if (step.IsFailure)
            {
                return step;
            }

            // TransferApproved is written to the outbox from stage 8, when Forecasting consumes it (#14): an integration
            // event is published only once at least one consumer exists (STAGE_5_PLAN section 4.1).
            await functions.ApproveAsync(order.Id, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<TransferDetails>> Handle(CancelTransferCommand command, CancellationToken cancellationToken) =>
        TransitionAsync(command.Id, TransferActions.Cancel, async (order, now, ct) =>
        {
            var step = order.Cancel(command.Reason, now);
            if (step.IsFailure)
            {
                return step;
            }

            // TransferCancelled follows TransferApproved into the outbox in stage 8 (#14).
            await functions.CancelAsync(order.Id, order.CancelReason!, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<TransferDetails>> Handle(DispatchTransferCommand command, CancellationToken cancellationToken) =>
        TransitionAsync(command.Id, TransferActions.Dispatch, async (order, now, ct) =>
        {
            var step = order.Dispatch(CurrentUserId(), HubAccess.Of(user), now);
            if (step.IsFailure)
            {
                return step;
            }

            await functions.DispatchAsync(order.Id, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<TransferDetails>> Handle(ReceiveTransferCommand command, CancellationToken cancellationToken) =>
        TransitionAsync(command.Id, TransferActions.Receive, async (order, now, ct) =>
        {
            var step = order.Receive(CurrentUserId(), HubAccess.Of(user), now);
            if (step.IsFailure)
            {
                return step;
            }

            await functions.ReceiveAsync(order.Id, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    private async Task<Result<TransferDetails>> TransitionAsync(
        long id, string action, Func<TransferOrder, DateTimeOffset, CancellationToken, Task<Result>> step, CancellationToken cancellationToken)
    {
        var order = await access.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return InventoryErrors.TransferNotFound;
        }

        // Approve and cancel are brand-scoped (STAGE_3_PLAN 3.3); the policy itself already passed at the endpoint.
        if (action is TransferActions.Approve or TransferActions.Cancel)
        {
            var brands = await access.BrandsAsync(order, cancellationToken).ConfigureAwait(false);
            if (!await access.MayAsync(action, order, brands).ConfigureAwait(false))
            {
                return TransferAccess.OutOfBrandScope;
            }
        }

        var done = await step(order, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (done.IsFailure)
        {
            return done.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await reader.ReadAsync(id, cancellationToken).ConfigureAwait(false))!;
    }

    private Guid CurrentUserId() => user.UserId ?? throw new InvalidOperationException("A transfer step needs a signed-in user.");
}

internal static class TransferCommandEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPost("/transfers", async (
                CreateTransferRequest request,
                ICommandHandler<CreateTransferCommand, TransferDetails> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreateTransferCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/inventory/transfers/{result.Value.Order.Id}", result.Value)
                    : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Inventory.Transfer.Create)
            .WithName("CreateTransfer")
            .WithSummary("Proposes a transfer between two hubs.")
            .WithDescription(
                "Creates a proposed transfer order with 1 to 50 lines, each part once, on an existing route. The number "
                + "(TR-YYYY-NNNNNN) is assigned by the database. Nothing is reserved until the order is approved. An unknown "
                + "route gives 422 Inventory.NoRoute; an unknown or deactivated part 422 Inventory.UnknownPart. Returns 201 "
                + "with the order and its Location. Permission Inventory.Transfer.Create.")
            .Accepts<CreateTransferRequest>("application/json")
            .Produces<TransferDetails>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new CreateTransferRequest(FromWarehouseId: 3, ToWarehouseId: 5, IsUrgent: false, Lines: [new CreateTransferLine(1042, 11)]))
            .WithResponseExample(StatusCodes.Status201Created, InventoryExamples.TransferDetails)
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                "Request.ToWarehouseId", "The recipient hub must differ from the donor hub."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, InventoryExamples.Problem(TransferErrors.NoRoute));

        MapTransition<ApproveTransferCommand>(group, "approve", id => new ApproveTransferCommand(id))
            .RequireAuthorization(Permissions.Inventory.Transfer.Approve)
            .WithName("ApproveTransfer")
            .WithSummary("Approves a proposed transfer.")
            .WithDescription(
                "UC01 steps 10–11: approves a proposed order and places firm reservations at the donor for every line, in one "
                + "transaction. A donor that would fall below its safety stock is refused with 409 SS201 (urgent orders may "
                + "use the buffer, US10); not enough available stock with 409 SS200; an order that is not proposed with 409 "
                + "SS300. A brand manager may approve only orders whose parts are all of their brands (403 "
                + "Inventory.OutOfBrandScope). Permission Inventory.Transfer.Approve (any brand with Inventory.Transfer.ApproveAnyBrand).")
            .WithResponseExample(StatusCodes.Status403Forbidden, InventoryExamples.Problem(TransferAccess.OutOfBrandScope))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(Error.Conflict("SS201",
                "Donor warehouse 3 would fall below its safety stock for part 1042 (available 14.000, safety 6.000, transfer 11.000)")));

        group.MapPost("/transfers/{id:long}/cancel", async (
                [Description("Transfer order id.")] long id,
                CancelTransferRequest request,
                ICommandHandler<CancelTransferCommand, TransferDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(new CancelTransferCommand(id, request.Reason), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Transfer.Cancel)
            .WithName("CancelTransfer")
            .WithSummary("Cancels a proposed or approved transfer.")
            .WithDescription(
                "Cancels the order with a reason and releases its firm reservations. A dispatched or received order cannot "
                + "be cancelled (409 SS300). A brand manager may cancel only orders whose parts are all of their brands (403 "
                + "Inventory.OutOfBrandScope). Permission Inventory.Transfer.Cancel (any brand with Inventory.Transfer.CancelAnyBrand).")
            .Accepts<CancelTransferRequest>("application/json")
            .Produces<TransferDetails>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CancelTransferRequest("Customer cancelled the service order"))
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.TransferDetails)
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem("Reason", "'Reason' must not be empty."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, InventoryExamples.Problem(TransferAccess.OutOfBrandScope))
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.TransferNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(
                InventoryErrors.InvalidTransition("transfer order", TransferStatus.Dispatched, "cancelled")));

        MapTransition<DispatchTransferCommand>(group, "dispatch", id => new DispatchTransferCommand(id))
            .RequireAuthorization(Permissions.Inventory.Transfer.Dispatch)
            .WithName("DispatchTransfer")
            .WithSummary("Dispatches an approved transfer.")
            .WithDescription(
                "US11: a keeper of the donor hub (or a user without a home hub) dispatches the order: the firm reservations are "
                + "consumed and TRANSFER_OUT movements posted; the stock is then in transit. A keeper of another hub gets 403 "
                + "SS302; an order that is not approved 409 SS300. Permission Inventory.Transfer.Dispatch.")
            .WithResponseExample(StatusCodes.Status403Forbidden, InventoryExamples.Problem(InventoryErrors.NotYourWarehouse))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(
                InventoryErrors.InvalidTransition("transfer order", TransferStatus.Proposed, "dispatched")));

        MapTransition<ReceiveTransferCommand>(group, "receive", id => new ReceiveTransferCommand(id))
            .RequireAuthorization(Permissions.Inventory.Transfer.Receive)
            .WithName("ReceiveTransfer")
            .WithSummary("Receives a dispatched transfer.")
            .WithDescription(
                "US11: a keeper of the recipient hub (or a user without a home hub) receives the order: TRANSFER_IN movements "
                + "are posted at the recipient. A keeper of another hub gets 403 SS302; an order that is not dispatched 409 "
                + "SS300. Permission Inventory.Transfer.Receive.")
            .WithResponseExample(StatusCodes.Status403Forbidden, InventoryExamples.Problem(InventoryErrors.NotYourWarehouse))
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(
                InventoryErrors.InvalidTransition("transfer order", TransferStatus.Approved, "received")));
    }

    /// <summary>POST /transfers/{id}/{step}: a transition without a body, with the responses all of them share.</summary>
    private static RouteHandlerBuilder MapTransition<TCommand>(IEndpointRouteBuilder group, string step, Func<long, TCommand> create)
        where TCommand : ICommand<TransferDetails> =>
        group.MapPost($"/transfers/{{id:long}}/{step}", async (
                [Description("Transfer order id.")] long id,
                ICommandHandler<TCommand, TransferDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(create(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .Produces<TransferDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.TransferDetails)
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.TransferNotFound));
}
