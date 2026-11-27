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
using SmartSpare.Modules.Fleet.Domain;

namespace SmartSpare.Modules.Fleet.Features.Machines;

/// <summary>Machine statuses as the API names them (values of <see cref="MachineSummary.MachineStatus"/>).</summary>
internal static class MachineStatusNames
{
    public static IReadOnlyList<string> All { get; } = ["active", "inactive", "decommissioned"];
}

/// <summary>The machine list (UC02 step 1): partial VIN, home hub, machine status and due status, one page at a time.</summary>
internal sealed record ListMachinesQuery(string? Search, short? WarehouseId, string? Status, string? DueStatus, int Page, int PageSize)
    : IQuery<PagedResponse<MachineSummary>>;

internal sealed class ListMachinesQueryValidator : AbstractValidator<ListMachinesQuery>
{
    public ListMachinesQueryValidator()
    {
        RuleFor(q => q.Search).MaximumLength(FleetSearch.MaxSearchLength);
        RuleFor(q => q.WarehouseId).GreaterThan((short)0).When(q => q.WarehouseId is not null);
        RuleFor(q => q.Status).Must(s => MachineStatusNames.All.Contains(s!)).When(q => q.Status is not null)
            .WithMessage($"'Status' must be one of: {string.Join(", ", MachineStatusNames.All)}.");
        RuleFor(q => q.DueStatus).Must(s => MaintenanceDueStatuses.All.Contains(s!)).When(q => q.DueStatus is not null)
            .WithMessage($"'Due Status' must be one of: {string.Join(", ", MaintenanceDueStatuses.All)}.");
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, FleetSearch.MaxPageSize);
    }
}

/// <summary>The machine card (WF03).</summary>
internal sealed record GetMachineQuery(long Id) : IQuery<MachineDetails>;

/// <summary>The reading history of a machine, newest first.</summary>
internal sealed record ListReadingsQuery(long MachineId, int Page, int PageSize) : IQuery<PagedResponse<ReadingRow>>;

internal sealed class ListReadingsQueryValidator : AbstractValidator<ListReadingsQuery>
{
    public ListReadingsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, FleetSearch.MaxPageSize);
    }
}

internal static class MachineQueryEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/machines", async (
                IQueryHandler<ListMachinesQuery, PagedResponse<MachineSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Text contained in the VIN, case-insensitive (a partial VIN from the machine plate works).")] string? search = null,
                [Description("Home hub id; empty lists every hub.")] short? warehouseId = null,
                [Description("Machine status: active, inactive or decommissioned; empty lists every status.")] string? status = null,
                [Description("Due status of the most urgent plan: overdue, dueSoon, planned or ok; empty lists every machine.")] string? dueStatus = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Rows per page, 1 to 100.")] int pageSize = FleetSearch.DefaultPageSize) =>
                (await handler.Handle(new ListMachinesQuery(search, warehouseId, status, dueStatus, page, pageSize), cancellationToken)
                    .ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Fleet.Machine.View)
            .WithName("ListMachines")
            .WithSummary("Lists customer machines with their engine hours and next service.")
            .WithDescription(
                "The installed base: VIN, model, brand, type, pseudonymous customer code, home hub, current engine hours and the "
                + "time of the latest reading. nextService is the most urgent maintenance plan of the machine with its due "
                + "calculation (MAINTENANCE_PLANNING.md): next engine-hour threshold, remaining hours, projected and calendar due "
                + "dates, the earlier of the two, and a status (overdue, dueSoon, planned, ok). Ordered by urgency, then VIN; "
                + "totalCount counts all matches. Permission Fleet.Machine.View.")
            .Produces<PagedResponse<MachineSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<MachineSummary>([FleetExamples.Machine], 1, FleetSearch.DefaultPageSize, 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem(
                nameof(ListMachinesQuery.DueStatus), "'Due Status' must be one of: overdue, dueSoon, planned, ok."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized);

        group.MapGet("/machines/{id:long}", async (
                [Description("Machine id.")] long id,
                IQueryHandler<GetMachineQuery, MachineDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(new GetMachineQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Fleet.Machine.View)
            .WithName("GetMachine")
            .WithSummary("Returns the machine card with every maintenance plan and its due calculation.")
            .WithDescription(
                "The machine card of the engineer's tablet (WF03): machine data, operating condition and its interval factor, the "
                + "average engine hours per day over the last year of readings, every active plan of the model with its due "
                + "calculation (most urgent first) and the five latest readings. Permission Fleet.Machine.View.")
            .Produces<MachineDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.MachineDetails)
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.MachineNotFound));

        group.MapGet("/machines/{id:long}/readings", async (
                [Description("Machine id.")] long id,
                IQueryHandler<ListReadingsQuery, PagedResponse<ReadingRow>> handler,
                CancellationToken cancellationToken,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Rows per page, 1 to 100.")] int pageSize = FleetSearch.DefaultPageSize) =>
                (await handler.Handle(new ListReadingsQuery(id, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Fleet.Machine.View)
            .WithName("ListMachineReadings")
            .WithSummary("Lists the engine-hour readings of a machine, newest first.")
            .WithDescription(
                "The odometer history (US05): time, engine hours, source (manual, telematics, serviceVisit, import) and the user "
                + "who recorded it. Readings are never edited; decreasing, implausible and out-of-order values were refused when "
                + "they were entered (SS100–SS103). Permission Fleet.Machine.View.")
            .Produces<PagedResponse<ReadingRow>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<ReadingRow>([FleetExamples.Reading], 1, FleetSearch.DefaultPageSize, 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem(
                nameof(ListReadingsQuery.PageSize), "'Page Size' must be between 1 and 100. You entered 500."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.MachineNotFound));
    }
}
