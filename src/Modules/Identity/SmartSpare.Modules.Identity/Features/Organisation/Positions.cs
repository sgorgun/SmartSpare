using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Identity;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Organisation;

internal static class VersionErrors
{
    public static readonly Error Required = Error.Validation(
        "Identity.VersionRequired", "Send the version you read as If-Match, so a concurrent change is not overwritten.");
}

internal sealed record ListPositionsQuery(string? Search, bool Archived, int Page, int PageSize) : IQuery<PagedResponse<PositionSummary>>;

internal sealed class ListPositionsQueryValidator : AbstractValidator<ListPositionsQuery>
{
    public ListPositionsQueryValidator()
    {
        RuleFor(q => q.Search).MaximumLength(50);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, UserLimits.MaxPageSize);
    }
}

internal sealed record GetPositionQuery(short Id) : IQuery<PositionSummary>;

internal sealed record CreatePositionCommand(CreatePositionRequest Request) : ICommand<PositionSummary>;

internal sealed class CreatePositionCommandValidator : AbstractValidator<CreatePositionCommand>
{
    public CreatePositionCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreatePositionRequestValidator());
}

internal sealed record UpdatePositionCommand(short Id, UpdatePositionRequest Request, uint? Version) : ICommand<PositionSummary>;

internal sealed class UpdatePositionCommandValidator : AbstractValidator<UpdatePositionCommand>
{
    public UpdatePositionCommandValidator() => RuleFor(c => c.Request).SetValidator(new UpdatePositionRequestValidator());
}

internal sealed record ArchivePositionCommand(short Id, uint? Version) : ICommand<PositionSummary>;

internal sealed record RestorePositionCommand(short Id, uint? Version) : ICommand<PositionSummary>;

/// <summary>
/// Positions and their role templates (ADR 0026). A template change rotates the stamps of every account in the
/// position, so their sessions get the new roles at the next check; the last administrator position keeps its role.
/// Written as <c>app_admin</c> for the transaction only.
/// </summary>
internal sealed class PositionAdministration(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, AdminSession admin, AccountStamps stamps, AdministratorGuard administrators,
    ICurrentUser currentUser, TimeProvider clock, IQueryHandler<GetPositionQuery, PositionSummary> reader)
    : ICommandHandler<CreatePositionCommand, PositionSummary>,
      ICommandHandler<UpdatePositionCommand, PositionSummary>,
      ICommandHandler<ArchivePositionCommand, PositionSummary>,
      ICommandHandler<RestorePositionCommand, PositionSummary>
{
    public async Task<Result<PositionSummary>> Handle(CreatePositionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await RoleIdsAsync(command.Request.Roles, cancellationToken).ConfigureAwait(false);
        if (roles.IsFailure)
        {
            return roles.Error;
        }

        var created = Position.Create(command.Request.Code, command.Request.Name, command.Request.Description, roles.Value);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await db.Set<Position>().AsNoTracking().AnyAsync(p => p.Code == created.Value.Code, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.CodeTaken;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        db.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetPositionQuery(created.Value.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PositionSummary>> Handle(UpdatePositionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var roles = await RoleIdsAsync(command.Request.Roles, cancellationToken).ConfigureAwait(false);
        if (roles.IsFailure)
        {
            return roles.Error;
        }

        return await ChangeAsync(command.Id, command.Version, async position =>
        {
            var wasAdministrator = await administrators.GrantsAdministratorAsync(position.RoleIds, cancellationToken).ConfigureAwait(false);
            var staysAdministrator = await administrators.GrantsAdministratorAsync(roles.Value, cancellationToken).ConfigureAwait(false);
            if (wasAdministrator && !staysAdministrator
                && !await administrators.AnotherAdministratorExistsAsync(null, position.Id, cancellationToken).ConfigureAwait(false))
            {
                return OrganisationErrors.LastAdministrator;
            }

            var changed = position.Update(command.Request.Name, command.Request.Description, roles.Value);
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            if (changed.Value)
            {
                await stamps.RotateForPositionAsync(position.Id, cancellationToken).ConfigureAwait(false);
            }

            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<PositionSummary>> Handle(ArchivePositionCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, command.Version, async position =>
        {
            if (await db.Set<Employee>().AsNoTracking().AnyAsync(e => e.PositionId == position.Id && !e.IsArchived, cancellationToken).ConfigureAwait(false))
            {
                return OrganisationErrors.PositionInUse;
            }

            position.Archive(clock.GetUtcNow(), currentUser.UserId);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<PositionSummary>> Handle(RestorePositionCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, command.Version, position =>
        {
            position.Restore();
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    private async Task<Result<PositionSummary>> ChangeAsync(short id, uint? version, Func<Position, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return VersionErrors.Required;
        }

        var position = await db.Set<Position>().Include(p => p.Roles).SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        if (position is null)
        {
            return OrganisationErrors.PositionNotFound;
        }

        db.Entry(position).Property<uint>("xmin").OriginalValue = version.Value;
        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        var result = await change(position).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // A template change writes only iam.position_role; touching the position row makes every change check the
        // If-Match version and give the position a new one (updated_at is set by its trigger).
        db.Entry(position).Property(p => p.UpdatedAt).IsModified = true;
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetPositionQuery(id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlySet<short>>> RoleIdsAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).ToArray();
        var ids = await db.Set<Role>().AsNoTracking().Where(r => distinct.Contains(r.Code)).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == distinct.Length ? ids.ToHashSet() : OrganisationErrors.UnknownRole;
    }
}
