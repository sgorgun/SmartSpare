using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;

namespace SmartSpare.Modules.Inventory.Features.PartAnalogues;

/// <summary>The direct analogues of parts at a hub (PR 7i3): direct interchangeable links and the current successor.</summary>
internal sealed record GetDirectAnaloguesQuery(IReadOnlyList<long> PartIds, short WarehouseId) : IQuery<IReadOnlyList<DirectAnalogues>>;

internal sealed class GetDirectAnaloguesQueryValidator : AbstractValidator<GetDirectAnaloguesQuery>
{
    public const int MaxParts = 100;

    public GetDirectAnaloguesQueryValidator()
    {
        RuleFor(q => q.PartIds).NotEmpty().Must(p => p.Count <= MaxParts).WithMessage($"Ask for 1 to {MaxParts} parts.");
        RuleForEach(q => q.PartIds).GreaterThan(0);
        RuleFor(q => q.WarehouseId).GreaterThan((short)0);
    }
}

/// <summary>
/// Read-only endpoints for the kit shortage of a service order (PR 7i3). The web client calls them through the gateway
/// and composes the answer with Fleet's order; the Fleet service never calls Inventory synchronously. Nothing is
/// substituted: substitution in reservations is #220.
/// </summary>
internal static class DirectAnalogueEndpoints
{
    private const string Rules =
        " Only direct interchangeable links and the current successor (the end of the part's supersession chain): indirect members of the "
        + "interchangeability group are left out, because transitivity is a forecasting assumption (ADR 0027), not confirmed compatibility. "
        + "available is on hand minus reserved at the hub, never raw on hand. Archived parts are left out. Read-only: nothing is substituted. "
        + "Permission Inventory.Stock.View (reads are never scoped by hub or brand).";

    private static readonly DirectAnalogues Example = new(1042, 3,
    [
        new DirectAnalogue(new PartRef(2210, "MANN", "W1170-12", "Oil filter, aftermarket", false), DirectAnalogueKinds.Interchangeable, 1m, 6m),
        new DirectAnalogue(new PartRef(1050, "JD", "RE504900", "Engine oil filter, new design", false), DirectAnalogueKinds.Successor, 1m, 0m),
    ]);

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/parts/{id:long}/direct-analogues", async (
                [Description("Part id.")] long id,
                [Description("The hub whose availability is shown.")] short warehouseId,
                IQueryHandler<GetDirectAnaloguesQuery, IReadOnlyList<DirectAnalogues>> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new GetDirectAnaloguesQuery([id], warehouseId), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Ok(result.Value[0]) : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Inventory.Stock.View)
            .WithTags("Parts")
            .WithName("GetDirectAnalogues")
            .WithSummary("Returns a part's direct analogues with their availability at a hub.")
            .WithDescription("For a short part of a service kit." + Rules)
            .Produces<DirectAnalogues>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, Example);

        v1.MapGet("/direct-analogues", async (
                [Description("The parts, repeated: partIds=1042&partIds=1055; 1 to 100.")][FromQuery] long[] partIds,
                [Description("The hub whose availability is shown.")] short warehouseId,
                IQueryHandler<GetDirectAnaloguesQuery, IReadOnlyList<DirectAnalogues>> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new GetDirectAnaloguesQuery(partIds, warehouseId), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Stock.View)
            .WithTags("Parts")
            .WithName("ListDirectAnalogues")
            .WithSummary("Returns the direct analogues of several parts at a hub in one request.")
            .WithDescription("For every short line of a service order in one call, one entry per part asked, in the order asked." + Rules)
            .Produces<IReadOnlyList<DirectAnalogues>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { Example });
    }
}
