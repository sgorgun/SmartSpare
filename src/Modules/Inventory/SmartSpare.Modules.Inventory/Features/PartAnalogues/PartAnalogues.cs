using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory.Features.PartAnalogues;

internal static class PartAnalogueErrors
{
    public static readonly Error ReasonRequired = Error.Validation(
        "Inventory.ReasonRequired", "Give a reason: removing or retyping a link changes future forecasts and is audited (ADR 0027).");

    public static readonly Error NotFound = Error.NotFound("Inventory.AnalogueNotFound", "The link does not exist.");

    public static readonly Error PartNotFound = Error.NotFound("Inventory.PartNotFound", "The part does not exist.");

    public static readonly Error ReplacingPartRequired = Error.Validation(
        "Inventory.ReplacingPartRequired", "For a supersession, say which of the two parts is the new one (replacingPartId).");
}

internal sealed record GetPartAnaloguesQuery(long PartId) : IQuery<PartAnaloguesView>;

internal sealed record CreatePartAnalogueCommand(PartAnalogueCreate Request) : ICommand<PartAnalogueDto>;

internal sealed class CreatePartAnalogueCommandValidator : AbstractValidator<CreatePartAnalogueCommand>
{
    public CreatePartAnalogueCommandValidator() => RuleFor(c => c.Request).SetValidator(new PartAnalogueCreateValidator());
}

internal sealed record UpdatePartAnalogueCommand(long Id, PartAnalogueUpdate Request, uint? Version) : ICommand<PartAnalogueDto>;

internal sealed class UpdatePartAnalogueCommandValidator : AbstractValidator<UpdatePartAnalogueCommand>
{
    public UpdatePartAnalogueCommandValidator() => RuleFor(c => c.Request).SetValidator(new PartAnalogueUpdateValidator());
}

internal sealed record RemovePartAnalogueCommand(long Id, RemoveAnalogueRequest Request, uint? Version) : ICommand;

/// <summary>
/// Writes part analogue links (ADR 0027). The rules over the other links (one per pair, one successor, no supersession
/// cycle) run first for a readable answer; the database enforces them again (SS502). Removing or retyping a link needs
/// the user's reason, which goes into the audit trail inside the write's transaction (decision 6).
/// </summary>
internal sealed class PartAnalogueHandler(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<CreatePartAnalogueCommand, PartAnalogueDto>, ICommandHandler<UpdatePartAnalogueCommand, PartAnalogueDto>,
      ICommandHandler<RemovePartAnalogueCommand>
{
    public async Task<Result<PartAnalogueDto>> Handle(CreatePartAnalogueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        if (await PartsUsableAsync([request.PartId, request.AnaloguePartId], cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var created = PartAnalogue.Create(request.PartId, request.AnaloguePartId, LinkType(request.LinkType), request.ScalingFactor,
            request.ValidFrom ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), request.Note, user.UserId);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var link = created.Value;
        var rules = AnalogueRules.Check(new AnalogueLinkFact(0, link.PartId, link.AnaloguePartId, link.LinkType), await FactsAsync(cancellationToken).ConfigureAwait(false));
        if (rules.IsFailure)
        {
            return rules.Error;
        }

        db.Add(link);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await DtoAsync(link.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PartAnalogueDto>> Handle(UpdatePartAnalogueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        if (command.Version is null)
        {
            return CatalogErrors.VersionRequired;
        }

        var link = await db.Set<PartAnalogue>().SingleOrDefaultAsync(l => l.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return PartAnalogueErrors.NotFound;
        }

        db.Entry(link).Property<uint>("xmin").OriginalValue = command.Version.Value;
        var type = LinkType(request.LinkType);
        var newPart = link.PartId;
        if (type == AnalogueLinkType.Supersedes)
        {
            if (request.ReplacingPartId is not { } replacing || (replacing != link.PartId && replacing != link.AnaloguePartId))
            {
                return PartAnalogueErrors.ReplacingPartRequired;
            }

            newPart = replacing;
        }

        var retyped = link.Retype(type, newPart);
        if (retyped && string.IsNullOrWhiteSpace(request.Reason))
        {
            return PartAnalogueErrors.ReasonRequired;
        }

        var details = link.ChangeDetails(request.ScalingFactor, request.ValidFrom, request.Note);
        if (details.IsFailure)
        {
            return details.Error;
        }

        if (retyped)
        {
            var rules = AnalogueRules.Check(new AnalogueLinkFact(link.Id, link.PartId, link.AnaloguePartId, link.LinkType), await FactsAsync(cancellationToken).ConfigureAwait(false));
            if (rules.IsFailure)
            {
                return rules.Error;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            await AuditReason.SetAsync(db, request.Reason, cancellationToken).ConfigureAwait(false);
        }

        db.Entry(link).Property(l => l.UpdatedAt).IsModified = true;   // checks If-Match and gives a new version even if nothing changed
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await DtoAsync(link.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> Handle(RemovePartAnalogueCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Request?.Reason))
        {
            return PartAnalogueErrors.ReasonRequired;
        }

        if (command.Version is null)
        {
            return CatalogErrors.VersionRequired;
        }

        var link = await db.Set<PartAnalogue>().SingleOrDefaultAsync(l => l.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return PartAnalogueErrors.NotFound;
        }

        db.Entry(link).Property<uint>("xmin").OriginalValue = command.Version.Value;
        await AuditReason.SetAsync(db, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        db.Remove(link);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    internal static AnalogueLinkType LinkType(string linkType) =>
        linkType == AnalogueLinkTypes.Supersedes ? AnalogueLinkType.Supersedes : AnalogueLinkType.Interchangeable;

    private async Task<Error?> PartsUsableAsync(long[] partIds, CancellationToken cancellationToken)
    {
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).Select(p => new { p.Id, p.IsArchived })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (parts.Count != partIds.Distinct().Count())
        {
            return PartAnalogueErrors.PartNotFound;
        }

        return parts.Exists(p => p.IsArchived) ? CatalogErrors.Archived("part") : null;
    }

    private async Task<IReadOnlyCollection<AnalogueLinkFact>> FactsAsync(CancellationToken cancellationToken) =>
        await db.Set<PartAnalogue>().AsNoTracking().Select(l => new AnalogueLinkFact(l.Id, l.PartId, l.AnaloguePartId, l.LinkType))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<PartAnalogueDto> DtoAsync(long id, CancellationToken cancellationToken) =>
        await db.Set<PartAnalogue>().AsNoTracking().Where(l => l.Id == id)
            .Select(l => new PartAnalogueDto(l.Id, l.PartId, l.AnaloguePartId,
                l.LinkType == AnalogueLinkType.Supersedes ? AnalogueLinkTypes.Supersedes : AnalogueLinkTypes.Interchangeable,
                l.ScalingFactor, l.ValidFrom, l.Note, Versions.Format(EF.Property<uint>(l, "xmin"))))
            .SingleAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>The part analogue endpoints under <c>/api/v1/inventory</c> (ADR 0027, PR 7i2a).</summary>
internal static class PartAnalogueEndpoints
{
    private static readonly PartRef ExamplePart = new(1042, "JD", "RE504836", "Engine oil filter", false);

    private static readonly PartAnaloguesView ExampleView = new(
        ExamplePart,
        [new PartAnalogueLink(7, AnalogueRelations.Interchangeable, new PartRef(2210, "MANN", "W 1170/12", "Oil filter, aftermarket", false), 1m, new DateOnly(2026, 11, 1), null, "8841")],
        [ExamplePart, new PartRef(2210, "MANN", "W 1170/12", "Oil filter, aftermarket", false)],
        [ExamplePart]);

    private static readonly PartAnalogueDto ExampleDto = new(7, 1042, 2210, AnalogueLinkTypes.Interchangeable, 1m, new DateOnly(2026, 11, 1), null, "8841");

    private const string Rules =
        " ADR 0027: an interchangeable link works both ways and its group is the connected component; a supersession points from the new part to the old one, "
        + "with at most one successor per part and no cycles; one link per pair; archived parts cannot be linked (422 SS500). Rule violations are 422 SS502.";

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/parts/{id:long}/analogues", async (
                [Description("Part id.")] long id, IQueryHandler<GetPartAnaloguesQuery, PartAnaloguesView> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new GetPartAnaloguesQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Part.View)
            .WithTags("Parts")
            .WithName("GetPartAnalogues")
            .WithSummary("Returns a part's analogues.")
            .WithDescription("The part's links seen from it, its interchangeability group and its supersession chain to the current part. Permission Inventory.Part.View." + Rules)
            .Produces<PartAnaloguesView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, ExampleView);

        v1.MapPost("/part-analogues", async (PartAnalogueCreate request, ICommandHandler<CreatePartAnalogueCommand, PartAnalogueDto> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreatePartAnalogueCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Created($"/api/v1/inventory/part-analogues/{result.Value.Id}", result.Value) : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Inventory.Part.Edit)
            .WithTags("Parts")
            .WithName("CreatePartAnalogue")
            .WithSummary("Links two parts.")
            .WithDescription("An interchangeable link or a supersession (partId replaces analoguePartId). Permission Inventory.Part.Edit." + Rules)
            .Accepts<PartAnalogueCreate>("application/json")
            .Produces<PartAnalogueDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new PartAnalogueCreate(1042, 2210, AnalogueLinkTypes.Interchangeable, 1m, null, null))
            .WithResponseExample(StatusCodes.Status201Created, ExampleDto)
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OpenApiConventions.Problem(AnalogueRules.SecondSuccessor));

        v1.MapPut("/part-analogues/{id:long}", async (
                [Description("Link id.")] long id, PartAnalogueUpdate request,
                [Description("The version read, as its ETag.")][FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<UpdatePartAnalogueCommand, PartAnalogueDto> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new UpdatePartAnalogueCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Part.Edit)
            .WithTags("Parts")
            .WithName("UpdatePartAnalogue")
            .WithSummary("Changes a link.")
            .WithDescription(
                "Needs If-Match. Factor, valid-from and note change freely; changing the type or the direction of a supersession changes future forecasts, "
                + "so it needs the user's reason (400 Inventory.ReasonRequired), which goes into the audit trail. Permission Inventory.Part.Edit." + Rules)
            .Accepts<PartAnalogueUpdate>("application/json")
            .Produces<PartAnalogueDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new PartAnalogueUpdate(AnalogueLinkTypes.Supersedes, 1042, 1m, new DateOnly(2026, 11, 1), null, "MANN confirmed W 1170/12 replaces the OEM filter"))
            .WithResponseExample(StatusCodes.Status200OK, ExampleDto)
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.Problem(PartAnalogueErrors.ReasonRequired));

        v1.MapPost("/part-analogues/{id:long}/remove", async (
                [Description("Link id.")] long id, RemoveAnalogueRequest request,
                [Description("The version read, as its ETag.")][FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<RemovePartAnalogueCommand> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new RemovePartAnalogueCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Inventory.Part.Edit)
            .WithTags("Parts")
            .WithName("RemovePartAnalogue")
            .WithSummary("Removes a link, with a reason.")
            .WithDescription(
                "Needs If-Match and the user's reason (400 Inventory.ReasonRequired): removing a link changes future forecasts (history is never rewritten), "
                + "so the reason goes into the audit trail. A POST action rather than DELETE, so the reason travels in the body, not in the URL. Permission Inventory.Part.Edit.")
            .Accepts<RemoveAnalogueRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new RemoveAnalogueRequest("The aftermarket filter failed the pressure test"))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.Problem(PartAnalogueErrors.ReasonRequired));
    }
}
