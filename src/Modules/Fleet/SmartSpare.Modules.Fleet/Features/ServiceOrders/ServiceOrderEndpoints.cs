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
            .RequireAuthorization(AppPolicies.ReadFleet)
            .WithName("ListServiceOrders")
            .WithSummary("Lists service orders, newest first.")
            .WithDescription(
                "Planned services and unplanned repairs with machine, plan, hub, status and kit status. kitStatus is reserving while "
                + "Inventory's answer to the draft is pending, then reserved, partial or notHeld by the held quantities; issued once "
                + "completed, released once cancelled. Policy ReadFleet.")
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
            .RequireAuthorization(AppPolicies.ReadFleet)
            .WithName("GetServiceOrder")
            .WithSummary("Returns a service order with its kit and the actions the current user may take.")
            .WithDescription(
                "The order, its lines (required, held at the hub, issued, missing; part data from Fleet's catalogue copy), when "
                + "Inventory answered the kit reservation, and allowedActions computed with the same policies and state rules as "
                + "the commands, so a screen offers only what the API accepts. Policy ReadFleet.")
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
            .RequireAuthorization(AppPolicies.ManageServiceOrders)
            .WithName("CreateServiceOrder")
            .WithSummary("Creates a planned service or an unplanned repair; Inventory then reserves its kit.")
            .WithDescription(
                "With maintenancePlanId: a planned service of a plan of the machine's model, drafted by fleet.create_service_order_draft "
                + "at the plan's next engine-hour threshold (the due calculation), with the plan's kit; a plan that already has an open "
                + "order is a 409 (Fleet.ServiceOrderExists). With lines: an unplanned repair with parts from Fleet's catalogue copy "
                + "(422 Fleet.UnknownPart otherwise). Either way the order starts as a draft whose kit Inventory reserves right after "
                + "(kitStatus reserving). Policy ManageServiceOrders (service engineers, brand managers, administrators), the roles of "
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
            .RequireAuthorization(AppPolicies.RecordServiceWork)
            .WithName("StartServiceOrder")
            .WithSummary("Marks a service order as in progress.")
            .WithDescription(
                "A draft or parts-reserved order goes to in progress; any other status is a 409 with code SS300 (the state machine "
                + "of fleet.trg_service_order_status_guard). Policy RecordServiceWork (service engineers, administrators).")
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
    }
}
