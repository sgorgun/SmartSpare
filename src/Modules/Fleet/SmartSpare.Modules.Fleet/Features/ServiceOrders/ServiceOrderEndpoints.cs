using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Infrastructure.Queries;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>Service orders, newest first, filtered by status, hub and machine.</summary>
internal sealed record ListServiceOrdersQuery(string? Status, short? WarehouseId, long? MachineId, int Page, int PageSize)
    : IQuery<PagedResponse<ServiceOrderSummary>>;

internal sealed class ListServiceOrdersQueryValidator : AbstractValidator<ListServiceOrdersQuery>
{
    public ListServiceOrdersQueryValidator()
    {
        RuleFor(q => q.Status).Must(s => ServiceOrderStatuses.All.Contains(s!)).When(q => q.Status is not null)
            .WithMessage($"'Status' must be one of: {string.Join(", ", ServiceOrderStatuses.All)}.");
        RuleFor(q => q.WarehouseId).GreaterThan((short)0).When(q => q.WarehouseId is not null);
        RuleFor(q => q.MachineId).GreaterThan(0).When(q => q.MachineId is not null);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, FleetSearch.MaxPageSize);
    }
}

/// <summary>One service order with its kit and allowed actions.</summary>
internal sealed record GetServiceOrderQuery(long Id) : IQuery<ServiceOrderDetails>;

/// <summary>A new planned service or unplanned repair (STAGE_6_PLAN 3.2).</summary>
internal sealed record CreateServiceOrderCommand(CreateServiceOrderRequest Request) : ICommand<long>;

internal sealed class CreateServiceOrderCommandValidator : AbstractValidator<CreateServiceOrderCommand>
{
    public CreateServiceOrderCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateServiceOrderRequestValidator());
}

/// <summary>
/// Creates a service order and hands its kit to Inventory (<c>ServiceOrderDrafted</c>, in the same transaction).
/// A planned service is drafted by <c>fleet.create_service_order_draft</c> at the plan's next threshold D from the due
/// calculation, so a later automatic draft never duplicates it; a plan with an open order is a conflict. A repair is
/// written by EF with parts from Fleet's catalogue copy.
/// </summary>
internal sealed class CreateServiceOrderCommandHandler(
    ServiceOrderStore store, FleetDbContext db, IUnitOfWork<FleetDbContext> unitOfWork, ICurrentUser user, TimeProvider time)
    : ICommandHandler<CreateServiceOrderCommand, long>
{
    public async Task<Result<long>> Handle(CreateServiceOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        if (await store.MachineAsync(request.MachineId, cancellationToken).ConfigureAwait(false) is not { } machine)
        {
            return FleetErrors.MachineNotFound;
        }

        if (!machine.IsActive)
        {
            return FleetErrors.MachineNotActive;
        }

        long orderId;
        if (request.MaintenancePlanId is { } planId)
        {
            if (await store.PlanAsync(planId, cancellationToken).ConfigureAwait(false) is not { } plan || plan.ModelId != machine.ModelId)
            {
                return FleetErrors.MaintenancePlanNotFound;
            }

            var input = (await MaintenanceDueInputs.LoadAsync(db, [machine.Id], null, cancellationToken).ConfigureAwait(false))
                .Single(i => i.MaintenancePlanId == planId);
            if (input.Input.OpenOrderThreshold is not null)
            {
                return FleetErrors.ServiceOrderExists;
            }

            var due = MaintenanceDueCalculator.Calculate(input.Input, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            orderId = await store.DraftFromPlanAsync(machine.Id, planId, due.DueEngineHours, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var lines = request.Lines!;
            var active = await store.ActivePartsAsync([.. lines.Select(l => l.PartId)], cancellationToken).ConfigureAwait(false);
            if (lines.Any(l => !active.Contains(l.PartId)))
            {
                return FleetErrors.UnknownPart;
            }

            var repair = ServiceOrder.DraftRepair(machine.Id, machine.HomeWarehouseId, machine.CurrentEngineHours, user.UserId ?? Guid.Empty,
                [.. lines.Select(l => new ServiceOrderLineDraft(l.PartId, l.Quantity))]);
            if (repair.IsFailure)
            {
                return repair.Error;
            }

            store.Add(repair.Value);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            orderId = repair.Value.Id;
        }

        var order = await store.ReadAsync(orderId, cancellationToken).ConfigureAwait(false);
        unitOfWork.AddIntegrationEvent(ServiceOrderEvents.Drafted(order, user.UserId ?? Guid.Empty, time.GetUtcNow()));
        return orderId;
    }
}

/// <summary>The engineer starts the work on an order that has not started.</summary>
internal sealed record StartServiceOrderCommand(long Id) : ICommand<long>;

internal sealed class StartServiceOrderCommandHandler(ServiceOrderStore store) : ICommandHandler<StartServiceOrderCommand, long>
{
    public async Task<Result<long>> Handle(StartServiceOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.TrackedAsync(command.Id, cancellationToken).ConfigureAwait(false) is not { } order)
        {
            return FleetErrors.ServiceOrderNotFound;
        }

        var started = order.Start();
        return started.IsSuccess ? order.Id : started.Error;
    }
}

/// <summary>Completes an order at the given engine hours (STAGE_6_PLAN 3.2, 5.4).</summary>
internal sealed record CompleteServiceOrderCommand(long Id, decimal EngineHours) : ICommand<long>;

internal sealed class CompleteServiceOrderCommandValidator : AbstractValidator<CompleteServiceOrderCommand>
{
    public CompleteServiceOrderCommandValidator() =>
        RuleFor(c => c.EngineHours).InclusiveBetween(0, RecordReadingRequestValidator.MaxHours)
            .Must(h => decimal.Round(h, 1) == h).WithMessage("Engine hours have at most one decimal.");
}

/// <summary>
/// Completes an open order: the aggregate checks the state and the hours, <c>fleet.complete_service_order</c> closes it
/// (role check and state guard in the database), and <c>ServiceOrderCompleted</c> goes to Inventory in the same transaction,
/// which issues the kit (shortfall rule F7, also while the reservation is still pending). Hours above the machine's current
/// hours are recorded first as a service-visit reading, which may draft further services. An identical repeat of a
/// completion that succeeded is a retry: nothing happens again (STAGE_6_PLAN 6h).
/// </summary>
internal sealed class CompleteServiceOrderCommandHandler(
    ServiceOrderStore store, ReadingStore readings, IUnitOfWork<FleetDbContext> unitOfWork, ICurrentUser user, TimeProvider time)
    : ICommandHandler<CompleteServiceOrderCommand, long>
{
    public async Task<Result<long>> Handle(CompleteServiceOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await store.ExistsAsync(command.Id, cancellationToken).ConfigureAwait(false))
        {
            return FleetErrors.ServiceOrderNotFound;
        }

        var order = await store.ReadAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (order.IsSameCompletion(command.EngineHours))
        {
            return order.Id;
        }

        var machine = (await store.MachineAsync(order.MachineId, cancellationToken).ConfigureAwait(false))!;
        var now = time.GetUtcNow();
        var completed = order.Complete(command.EngineHours, machine.CurrentEngineHours, now);
        if (completed.IsFailure)
        {
            return completed.Error;
        }

        var actor = user.UserId ?? Guid.Empty;
        if (command.EngineHours > machine.CurrentEngineHours)
        {
            var drafted = await readings.LogAsync(machine.Id, command.EngineHours, now, Domain.Machines.ReadingSource.ServiceVisit, cancellationToken)
                .ConfigureAwait(false);
            foreach (var draft in await readings.OrdersAsync([.. drafted.Select(d => d.ServiceOrderId)], cancellationToken).ConfigureAwait(false))
            {
                unitOfWork.AddIntegrationEvent(ServiceOrderEvents.Drafted(draft, actor, now));
            }
        }

        await store.CompleteAsync(order.Id, command.EngineHours, cancellationToken).ConfigureAwait(false);
        var kit = ServiceOrderEvents.Completed(order, actor, now);
        if (kit.Lines.Count > 0)
        {
            unitOfWork.AddIntegrationEvent(kit);
        }

        return order.Id;
    }
}

/// <summary>Cancels an open order with a reason (STAGE_6_PLAN 3.2, 5.5).</summary>
internal sealed record CancelServiceOrderCommand(long Id, string Reason) : ICommand<long>;

internal sealed class CancelServiceOrderCommandValidator : AbstractValidator<CancelServiceOrderCommand>
{
    public CancelServiceOrderCommandValidator() =>
        RuleFor(c => c.Reason).Must(r => !string.IsNullOrWhiteSpace(r)).WithMessage("A cancellation needs a reason.")
            .MaximumLength(CancelServiceOrderRequestValidator.MaxReasonLength);
}

/// <summary>
/// Cancels an open order: the aggregate checks the state, <c>fleet.cancel_service_order</c> cancels it (role check and
/// status guard in the database), and <c>ServiceOrderCancelled</c> goes to Inventory in the same transaction, which releases
/// the holds of its lines. A reservation still in flight is released when it reaches the cancelled order.
/// </summary>
internal sealed class CancelServiceOrderCommandHandler(ServiceOrderStore store, IUnitOfWork<FleetDbContext> unitOfWork, ICurrentUser user, TimeProvider time)
    : ICommandHandler<CancelServiceOrderCommand, long>
{
    public async Task<Result<long>> Handle(CancelServiceOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await store.ExistsAsync(command.Id, cancellationToken).ConfigureAwait(false))
        {
            return FleetErrors.ServiceOrderNotFound;
        }

        var order = await store.ReadAsync(command.Id, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var cancelled = order.Cancel(command.Reason, now);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        await store.CancelAsync(order.Id, order.CancelReason!, cancellationToken).ConfigureAwait(false);
        unitOfWork.AddIntegrationEvent(ServiceOrderEvents.Cancelled(order, order.CancelReason!, user.UserId ?? Guid.Empty, now));
        return order.Id;
    }
}

/// <summary>Sets the kit of an order that has not started (PR 6i, #140).</summary>
internal sealed record ChangeServiceOrderLinesCommand(long Id, IReadOnlyList<CreateServiceOrderLine> Lines) : ICommand<long>;

internal sealed class ChangeServiceOrderLinesCommandValidator : AbstractValidator<ChangeServiceOrderLinesCommand>
{
    public ChangeServiceOrderLinesCommandValidator() =>
        RuleFor(c => new ChangeServiceOrderLinesRequest(c.Lines)).SetValidator(new ChangeServiceOrderLinesRequestValidator()).OverridePropertyName("Lines");
}

/// <summary>
/// Edits the kit: the aggregate diffs the wanted lines against the current ones (a changed quantity becomes a new line),
/// the change is saved, and <c>ServiceOrderLinesChanged</c> goes to Inventory in the same transaction, which releases the
/// removed lines' holds and reserves the new lines. The same kit again changes nothing and publishes nothing.
/// </summary>
internal sealed class ChangeServiceOrderLinesCommandHandler(ServiceOrderStore store, IUnitOfWork<FleetDbContext> unitOfWork, ICurrentUser user, TimeProvider time)
    : ICommandHandler<ChangeServiceOrderLinesCommand, long>
{
    public async Task<Result<long>> Handle(ChangeServiceOrderLinesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.TrackedAsync(command.Id, cancellationToken).ConfigureAwait(false) is not { } order)
        {
            return FleetErrors.ServiceOrderNotFound;
        }

        var current = order.Lines.Select(l => l.PartId).ToHashSet();
        var newParts = command.Lines.Select(l => l.PartId).Where(p => !current.Contains(p)).ToList();
        if (newParts.Count > 0)
        {
            var active = await store.ActivePartsAsync(newParts, cancellationToken).ConfigureAwait(false);
            if (newParts.Any(p => !active.Contains(p)))
            {
                return FleetErrors.UnknownPart;
            }
        }

        var changed = order.ReplaceLines([.. command.Lines.Select(l => new ServiceOrderLineDraft(l.PartId, l.Quantity))]);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        if (changed.Value.IsEmpty)
        {
            return order.Id;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        unitOfWork.AddIntegrationEvent(ServiceOrderEvents.LinesChanged(order, changed.Value, user.UserId ?? Guid.Empty, time.GetUtcNow()));
        return order.Id;
    }
}

internal static class ServiceOrderEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/service-orders", async (
                IQueryHandler<ListServiceOrdersQuery, PagedResponse<ServiceOrderSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Status: draft, partsReserved, inProgress, completed or cancelled; empty lists every status.")] string? status = null,
                [Description("Hub id; empty lists every hub.")] short? warehouseId = null,
                [Description("Machine id; empty lists every machine.")] long? machineId = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Rows per page, 1 to 100.")] int pageSize = FleetSearch.DefaultPageSize) =>
                (await handler.Handle(new ListServiceOrdersQuery(status, warehouseId, machineId, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.View)
            .WithName("ListServiceOrders")
            .WithSummary("Lists service orders, newest first.")
            .WithDescription(
                "Planned services and unplanned repairs with machine, plan, hub, status and kit status. kitStatus is reserving while "
                + "Inventory's answer to the draft is pending, then reserved, partial or notHeld by the held quantities; issued once "
                + "completed, released once cancelled. Permission Fleet.ServiceOrder.View.")
            .Produces<PagedResponse<ServiceOrderSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<ServiceOrderSummary>([FleetExamples.ServiceOrder], 1, FleetSearch.DefaultPageSize, 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem(
                nameof(ListServiceOrdersQuery.Status), "'Status' must be one of: draft, partsReserved, inProgress, completed, cancelled."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized);

        group.MapGet("/service-orders/{id:long}", async (
                [Description("Service order id.")] long id,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(new GetServiceOrderQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.View)
            .WithName("GetServiceOrder")
            .WithSummary("Returns a service order with its kit and the actions the current user may take.")
            .WithDescription(
                "The order, its lines (required, held at the hub, issued, missing; part data from Fleet's catalogue copy), when "
                + "Inventory answered the kit reservation, and allowedActions computed with the same policies and state rules as "
                + "the commands, so a screen offers only what the API accepts. Permission Fleet.ServiceOrder.View.")
            .Produces<ServiceOrderDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.ServiceOrderDetails)
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.ServiceOrderNotFound));

        group.MapPost("/service-orders", async (
                CreateServiceOrderRequest request,
                ICommandHandler<CreateServiceOrderCommand, long> handler,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> details,
                CancellationToken cancellationToken) =>
            {
                var created = await handler.Handle(new CreateServiceOrderCommand(request), cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                {
                    return created.ToHttpResult();
                }

                var order = await details.Handle(new GetServiceOrderQuery(created.Value), cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/v1/fleet/service-orders/{created.Value}", order.Value);
            })
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.Create)
            .WithName("CreateServiceOrder")
            .WithSummary("Creates a planned service or an unplanned repair; Inventory then reserves its kit.")
            .WithDescription(
                "With maintenancePlanId: a planned service of a plan of the machine's model, drafted by fleet.create_service_order_draft "
                + "at the plan's next engine-hour threshold (the due calculation), with the plan's kit; a plan that already has an open "
                + "order is a 409 (Fleet.ServiceOrderExists). With lines: an unplanned repair with parts from Fleet's catalogue copy "
                + "(422 Fleet.UnknownPart otherwise). Either way the order starts as a draft whose kit Inventory reserves right after "
                + "(kitStatus reserving). Permission Fleet.ServiceOrder.Create, the roles of "
                + "fleet.create_service_order_draft.")
            .Accepts<CreateServiceOrderRequest>("application/json")
            .Produces<ServiceOrderDetails>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new CreateServiceOrderRequest(501, MaintenancePlanId: 22))
            .WithResponseExample(StatusCodes.Status201Created, FleetExamples.ServiceOrderDetails)
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem("Order", "Give either a maintenance plan or the parts of a repair, not both."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.MachineNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.ServiceOrderExists))
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OpenApiConventions.Problem(FleetErrors.UnknownPart));

        group.MapPost("/service-orders/{id:long}/start", async (
                [Description("Service order id.")] long id,
                ICommandHandler<StartServiceOrderCommand, long> handler,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> details,
                CancellationToken cancellationToken) =>
            {
                var started = await handler.Handle(new StartServiceOrderCommand(id), cancellationToken).ConfigureAwait(false);
                return started.IsFailure
                    ? started.ToHttpResult()
                    : (await details.Handle(new GetServiceOrderQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.Start)
            .WithName("StartServiceOrder")
            .WithSummary("Marks a service order as in progress.")
            .WithDescription(
                "A draft or parts-reserved order goes to in progress; any other status is a 409 with code SS300 (the state machine "
                + "of fleet.trg_service_order_status_guard). Permission Fleet.ServiceOrder.Start.")
            .Produces<ServiceOrderDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.ServiceOrderDetails with { Order = FleetExamples.ServiceOrder with { Status = ServiceOrderStatuses.InProgress } })
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.ServiceOrderNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.InvalidTransition(ServiceOrderStatus.Cancelled, "started")));

        group.MapPost("/service-orders/{id:long}/complete", async (
                [Description("Service order id.")] long id,
                CompleteServiceOrderRequest request,
                ICommandHandler<CompleteServiceOrderCommand, long> handler,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> details,
                CancellationToken cancellationToken) =>
            {
                var completed = await handler.Handle(new CompleteServiceOrderCommand(id, request.EngineHours), cancellationToken).ConfigureAwait(false);
                return completed.IsFailure
                    ? completed.ToHttpResult()
                    : (await details.Handle(new GetServiceOrderQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.Complete)
            .WithName("CompleteServiceOrder")
            .WithSummary("Completes a service order; Inventory then issues its kit.")
            .WithDescription(
                "An open order (draft, partsReserved or inProgress) is completed at the given engine hours, which may not be below "
                + "the hours that triggered it or the machine's current hours (400); higher hours are recorded as a service-visit "
                + "reading first. Inventory then issues the kit: the reserved quantities, and the unreserved rest up to the stock at "
                + "the hub; lines show what was issued and what is missing (the shortfall). This also applies while the kit is "
                + "still being reserved. Posting the same completion again returns the completed order (a safe retry); other "
                + "hours on a completed order, or completing a cancelled order, are a 409 with code SS300. Permission Fleet.ServiceOrder.Complete, "
                + "the roles of fleet.complete_service_order.")
            .Accepts<CompleteServiceOrderRequest>("application/json")
            .Produces<ServiceOrderDetails>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CompleteServiceOrderRequest(515))
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.CompletedServiceOrder)
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.Problem(FleetErrors.CompletionHoursTooLow))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.ServiceOrderNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.InvalidTransition(ServiceOrderStatus.Completed, "completed")));

        group.MapPut("/service-orders/{id:long}/lines", async (
                [Description("Service order id.")] long id,
                ChangeServiceOrderLinesRequest request,
                ICommandHandler<ChangeServiceOrderLinesCommand, long> handler,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> details,
                CancellationToken cancellationToken) =>
            {
                var changed = await handler.Handle(new ChangeServiceOrderLinesCommand(id, request.Lines ?? []), cancellationToken).ConfigureAwait(false);
                return changed.IsFailure
                    ? changed.ToHttpResult()
                    : (await details.Handle(new GetServiceOrderQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.Edit)
            .WithName("ChangeServiceOrderLines")
            .WithSummary("Sets the kit of a service order that has not started; Inventory adjusts the holds.")
            .WithDescription(
                "The body is the whole kit from now on. Parts that are missing are removed and Inventory releases their holds; new "
                + "parts are added and reserved; a part whose quantity changed is replaced by a new line, released and reserved again. "
                + "With new lines the kit is reserving again (kitStatus reserving; an order that was partsReserved returns to draft). "
                + "Sending the same kit again changes nothing. A repair keeps at least one line (400 Fleet.NoLines); a planned "
                + "service may lose all. Each part once (400), parts must be active in the catalogue (422 Fleet.UnknownPart). Once "
                + "the work has started, completed or cancelled the kit is fixed: 409 with code SS300. Permission Fleet.ServiceOrder.Edit.")
            .Accepts<ChangeServiceOrderLinesRequest>("application/json")
            .Produces<ServiceOrderDetails>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new ChangeServiceOrderLinesRequest([new CreateServiceOrderLine(1203, 1), new CreateServiceOrderLine(1210, 25)]))
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.EditedServiceOrder)
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem("Lines", "Each part appears only once."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.ServiceOrderNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.InvalidTransition(ServiceOrderStatus.InProgress, "changed")))
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OpenApiConventions.Problem(FleetErrors.UnknownPart));

        group.MapPost("/service-orders/{id:long}/cancel", async (
                [Description("Service order id.")] long id,
                CancelServiceOrderRequest request,
                ICommandHandler<CancelServiceOrderCommand, long> handler,
                IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails> details,
                CancellationToken cancellationToken) =>
            {
                var cancelled = await handler.Handle(new CancelServiceOrderCommand(id, request.Reason), cancellationToken).ConfigureAwait(false);
                return cancelled.IsFailure
                    ? cancelled.ToHttpResult()
                    : (await details.Handle(new GetServiceOrderQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(Permissions.Fleet.ServiceOrder.Cancel)
            .WithName("CancelServiceOrder")
            .WithSummary("Cancels a service order; Inventory then releases its kit.")
            .WithDescription(
                "An open order (draft, partsReserved or inProgress) is cancelled with a reason (1 to 300 characters); Inventory "
                + "releases the holds of its lines (kitStatus released). A kit reservation still in flight is released as soon as it "
                + "reaches the cancelled order, so no hold remains. Cancelling a completed or cancelled order is a 409 with code "
                + "SS300. Permission Fleet.ServiceOrder.Cancel, the roles of fleet.cancel_service_order.")
            .Accepts<CancelServiceOrderRequest>("application/json")
            .Produces<ServiceOrderDetails>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CancelServiceOrderRequest("Customer sold the machine"))
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.CancelledServiceOrder)
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem("Reason", "A cancellation needs a reason."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.ServiceOrderNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.InvalidTransition(ServiceOrderStatus.Completed, "cancelled")));
    }
}
