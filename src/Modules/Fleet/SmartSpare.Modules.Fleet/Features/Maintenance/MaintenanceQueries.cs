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

namespace SmartSpare.Modules.Fleet.Features.Maintenance;

/// <summary>The due-maintenance list (STAGE_6_PLAN 2.5): plans of active machines by due date.</summary>
internal sealed record DueMaintenanceQuery(short? WarehouseId, int HorizonDays, string? Status, int Page, int PageSize)
    : IQuery<PagedResponse<DueMaintenanceRow>>;

internal sealed class DueMaintenanceQueryValidator : AbstractValidator<DueMaintenanceQuery>
{
    public DueMaintenanceQueryValidator()
    {
        RuleFor(q => q.WarehouseId).GreaterThan((short)0).When(q => q.WarehouseId is not null);
        RuleFor(q => q.HorizonDays).InclusiveBetween(0, FleetSearch.MaxHorizonDays);
        RuleFor(q => q.Status).Must(s => MaintenanceDueStatuses.All.Contains(s!)).When(q => q.Status is not null)
            .WithMessage($"'Status' must be one of: {string.Join(", ", MaintenanceDueStatuses.All)}.");
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, FleetSearch.MaxPageSize);
    }
}

/// <summary>The OEM maintenance plans with their kits, optionally of one machine model.</summary>
internal sealed record ListMaintenancePlansQuery(int? ModelId) : IQuery<IReadOnlyList<MaintenancePlanResponse>>;

internal static class MaintenanceQueryEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/maintenance/due", async (
                IQueryHandler<DueMaintenanceQuery, PagedResponse<DueMaintenanceRow>> handler,
                CancellationToken cancellationToken,
                [Description("Home hub id; empty lists every hub.")] short? warehouseId = null,
                [Description("Planning horizon in days, 0 to 365: a plan due within it is dueSoon.")] int horizonDays = FleetSearch.DefaultHorizonDays,
                [Description("One status (overdue, dueSoon, planned or ok); empty lists overdue and dueSoon.")] string? status = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Rows per page, 1 to 100.")] int pageSize = FleetSearch.DefaultPageSize) =>
                (await handler.Handle(new DueMaintenanceQuery(warehouseId, horizonDays, status, page, pageSize), cancellationToken)
                    .ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(AppPolicies.ReadFleet)
            .WithName("ListDueMaintenance")
            .WithSummary("Lists the machines that need a planned service soon.")
            .WithDescription(
                "One row per active machine and maintenance plan with its due calculation (MAINTENANCE_PLANNING.md): the next "
                + "engine-hour threshold D = max((⌊L/E⌋+1)·E, ⌊h/E⌋·E) with E = round(interval × condition factor, 1), the "
                + "remaining hours, the projected date from the average daily usage, the calendar date (last service plus the "
                + "calendar interval) and the earlier of the two. overdue: the threshold or the calendar date has passed; dueSoon: "
                + "at most 50 h remain or the due date is within horizonDays; planned: an open service order exists. By default "
                + "overdue and dueSoon rows, ordered by due date (unknown dates last). Policy ReadFleet.")
            .Produces<PagedResponse<DueMaintenanceRow>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<DueMaintenanceRow>([FleetExamples.DueRow], 1, FleetSearch.DefaultPageSize, 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem(
                nameof(DueMaintenanceQuery.HorizonDays), "'Horizon Days' must be between 0 and 365. You entered 400."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized);

        group.MapGet("/maintenance-plans", async (
                IQueryHandler<ListMaintenancePlansQuery, IReadOnlyList<MaintenancePlanResponse>> handler,
                CancellationToken cancellationToken,
                [Description("Machine model id; empty lists the plans of every model.")] int? modelId = null) =>
                (await handler.Handle(new ListMaintenancePlansQuery(modelId), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(AppPolicies.ReadFleet)
            .WithName("ListMaintenancePlans")
            .WithSummary("Lists the OEM maintenance plans with their service kits.")
            .WithDescription(
                "Active OEM plans per machine model: name, interval in engine hours and, if the OEM sets one, in months (due by "
                + "whichever comes first), and the kit with part data from Fleet's copy of the catalogue (fed by "
                + "PartCatalogChanged). Plans are OEM reference data and are not changed through the API. Policy ReadFleet.")
            .Produces<IReadOnlyList<MaintenancePlanResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { FleetExamples.Plan })
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized);
    }
}
