using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Machines;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Features.Readings;

/// <summary>Records an engine-hour reading of a machine (UC02 steps 1–7, US05).</summary>
internal sealed record RecordReadingCommand(long MachineId, decimal Hours, DateTimeOffset? ReadAt, string? Source) : ICommand<RecordReadingResponse>;

internal sealed class RecordReadingCommandValidator : AbstractValidator<RecordReadingCommand>
{
    // The rules of the shared RecordReadingRequestValidator, which the form uses too.
    public RecordReadingCommandValidator()
    {
        RuleFor(c => c.Hours).InclusiveBetween(0, RecordReadingRequestValidator.MaxHours)
            .Must(h => decimal.Round(h, 1) == h).WithMessage("Engine hours have at most one decimal.");
        RuleFor(c => c.Source).Must(s => RecordReadingRequestValidator.AllowedSources.Contains(s!)).When(c => c.Source is not null)
            .WithMessage($"'Source' must be one of: {string.Join(", ", RecordReadingRequestValidator.AllowedSources)}.");
    }
}

/// <summary>Reading sources as the API names them.</summary>
internal static class ReadingSourceNames
{
    public static ReadingSource? Parse(string? source) => source switch
    {
        null or ReadingSources.Manual => ReadingSource.Manual,
        ReadingSources.Telematics => ReadingSource.Telematics,
        ReadingSources.ServiceVisit => ReadingSource.ServiceVisit,
        _ => null,
    };

    public static string Name(ReadingSource source) => source switch
    {
        ReadingSource.Telematics => ReadingSources.Telematics,
        ReadingSource.ServiceVisit => ReadingSources.ServiceVisit,
        ReadingSource.Import => ReadingSources.Import,
        _ => ReadingSources.Manual,
    };

    /// <summary>PostgreSQL keeps microseconds; a retry must compare equal to the stored time.</summary>
    public static DateTimeOffset ToDatabasePrecision(DateTimeOffset time) => new(time.Ticks - (time.Ticks % 10), time.Offset);
}

/// <summary>
/// STAGE_6_PLAN 3.1. Concurrency: the machine row is locked first, so readings of one machine are handled one after the
/// other and the checks below see the previous reading. Idempotency: an identical reading at the same time is a retry
/// and returns the stored one; a different value at the same time is a conflict. Then the aggregate gives the readable
/// SS100–SS104 answer, and <c>fleet.log_engine_hours</c> stores the reading (its triggers repeat every rule; a database
/// SS1xx error maps to the same 422) and drafts the services that became due. No integration event: drafts publish
/// <c>ServiceOrderDrafted</c> from PR 6g, when Inventory consumes it.
/// </summary>
internal sealed class RecordReadingCommandHandler(ReadingStore store, TimeProvider time)
    : ICommandHandler<RecordReadingCommand, RecordReadingResponse>
{
    public async Task<Result<RecordReadingResponse>> Handle(RecordReadingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (ReadingSourceNames.Parse(command.Source) is not { } source)
        {
            return FleetErrors.InvalidReadingSource;
        }

        if (!await store.LockMachineAsync(command.MachineId, cancellationToken).ConfigureAwait(false))
        {
            return FleetErrors.MachineNotFound;
        }

        var now = time.GetUtcNow();
        var readAt = ReadingSourceNames.ToDatabasePrecision(command.ReadAt ?? now);
        var existing = await store.ReadingAtAsync(command.MachineId, readAt, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.ReadingHours == command.Hours
                ? Response(existing, existing.ReadingHours, replayed: true, [])
                : FleetErrors.ReadingConflict;
        }

        var machine = await store.MachineAsync(command.MachineId, cancellationToken).ConfigureAwait(false);
        var last = await store.LastReadingAsync(command.MachineId, cancellationToken).ConfigureAwait(false);
        var allowed = machine.CheckReading(command.Hours, readAt, last, now);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var drafted = await store.LogAsync(command.MachineId, command.Hours, readAt, source, cancellationToken).ConfigureAwait(false);
        var stored = (await store.ReadingAtAsync(command.MachineId, readAt, cancellationToken).ConfigureAwait(false))!;
        var current = await store.MachineAsync(command.MachineId, cancellationToken).ConfigureAwait(false);
        return Response(stored, current.CurrentEngineHours, replayed: false, drafted);
    }

    private static RecordReadingResponse Response(EngineHoursReading reading, decimal engineHours, bool replayed, IReadOnlyList<DraftedOrder> drafted) =>
        new(new ReadingRow(reading.Id, reading.ReadAt, reading.ReadingHours, ReadingSourceNames.Name(reading.Source), reading.RecordedBy),
            engineHours, replayed,
            [.. drafted.Select(d => new DraftedServiceOrder(d.ServiceOrderId, d.ServiceOrderNumber, d.DueEngineHours, d.FullyReserved))]);
}

internal static class RecordReadingEndpoint
{
    /// <summary>POST /api/v1/fleet/machines/{id}/readings.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPost("/machines/{id:long}/readings", async (
                [Description("Machine id.")] long id,
                RecordReadingRequest request,
                ICommandHandler<RecordReadingCommand, RecordReadingResponse> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new RecordReadingCommand(id, request.Hours, request.ReadAt, request.Source), cancellationToken)
                    .ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.ToHttpResult();
                }

                return result.Value.Replayed
                    ? Results.Ok(result.Value)
                    : Results.Created($"/api/v1/fleet/machines/{id}/readings", result.Value);
            })
            .RequireAuthorization(AppPolicies.RecordServiceWork)
            .WithName("RecordReading")
            .WithSummary("Records an engine-hour reading and drafts the services that became due.")
            .WithDescription(
                "UC02 steps 1–7. The reading is checked against the machine's last reading: the machine must be active (SS102), "
                + "the time after the last reading (SS103) and at most five minutes ahead of the server clock (SS104), the "
                + "hours not below the last reading (SS100, the odometer-rollback case A1) and not rising faster than wall-clock "
                + "time (SS101). Each refusal is a 422 with the code. A valid reading is stored by fleet.log_engine_hours, which "
                + "drafts a service order with its kit for every plan due within 50 h (US05); the response lists them. "
                + "Concurrent readings of one machine are handled one after the other. Posting an identical reading again (same "
                + "time and hours) is a safe retry: 200 with the stored reading and replayed = true; a different value at the "
                + "same time is a 409. Policy RecordServiceWork (service engineers, administrators), the same roles as "
                + "fleet.log_engine_hours; no brand or home-hub scope.")
            .Accepts<RecordReadingRequest>("application/json")
            .Produces<RecordReadingResponse>(StatusCodes.Status201Created)
            .Produces<RecordReadingResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new RecordReadingRequest(512, new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.Zero), ReadingSources.Manual))
            .WithResponseExample(StatusCodes.Status201Created, FleetExamples.RecordedReading)
            .WithResponseExample(StatusCodes.Status200OK, FleetExamples.RecordedReading with { Replayed = true, DraftedOrders = [] })
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.ValidationProblem("Hours", "Engine hours have at most one decimal."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, FleetExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status403Forbidden, FleetExamples.Forbidden)
            .WithResponseExample(StatusCodes.Status404NotFound, OpenApiConventions.Problem(FleetErrors.MachineNotFound))
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(FleetErrors.ReadingConflict))
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OpenApiConventions.Problem(FleetErrors.ReadingDecreased));
}
