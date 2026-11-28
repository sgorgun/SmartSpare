using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Identity;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Organisation;

internal sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleSummary>>;

internal sealed record GetRoleQuery(short Id) : IQuery<RoleSummary>;

internal sealed record CreateRoleCommand(CreateRoleRequest Request) : ICommand<RoleSummary>;

internal sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateRoleRequestValidator());
}

internal sealed record UpdateRoleCommand(short Id, UpdateRoleRequest Request, uint? Version) : ICommand<RoleSummary>;

internal sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator() => RuleFor(c => c.Request).SetValidator(new UpdateRoleRequestValidator());
}

internal sealed record ArchiveRoleCommand(short Id, uint? Version) : ICommand<RoleSummary>;

internal sealed record RestoreRoleCommand(short Id, uint? Version) : ICommand<RoleSummary>;

internal sealed record ResetRoleCommand(short Id, uint? Version) : ICommand<RoleSummary>;

internal sealed record ConfirmRoleUpgradeCommand(short Id, uint? Version) : ICommand<RoleSummary>;

internal sealed record CopyRoleCommand(short Id, CopyRoleRequest Request) : ICommand<RoleSummary>;

internal sealed class CopyRoleCommandValidator : AbstractValidator<CopyRoleCommand>
{
    public CopyRoleCommandValidator() => RuleFor(c => c.Request).SetValidator(new CopyRoleRequestValidator());
}

/// <summary>
/// Roles as data (ADR 0028): create, change name and permissions, archive, restore, reset a seeded role to its shipped
/// permissions, copy a role. Seeded roles are editable like any other; only the locked ones (Administrator,
/// Superadministrator) are refused. Nobody changes a role they hold (403 SS301); a role or any position holding it must
/// not break separation of duties (409); a change of permissions rotates the stamps of every account whose position
/// holds the role, so sessions follow within the session check. Written as <c>app_admin</c>; the audit trigger records
/// each change, a reset included.
/// </summary>
internal sealed class RoleAdministration(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, AdminSession admin, RightsGuard guard, AccountStamps stamps,
    IQueryHandler<GetRoleQuery, RoleSummary> reader)
    : ICommandHandler<CreateRoleCommand, RoleSummary>, ICommandHandler<UpdateRoleCommand, RoleSummary>,
      ICommandHandler<ArchiveRoleCommand, RoleSummary>, ICommandHandler<RestoreRoleCommand, RoleSummary>,
      ICommandHandler<ResetRoleCommand, RoleSummary>, ICommandHandler<CopyRoleCommand, RoleSummary>,
      ICommandHandler<ConfirmRoleUpgradeCommand, RoleSummary>
{
    public async Task<Result<RoleSummary>> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var created = Role.Create(command.Request.Code, command.Request.Name, command.Request.Description, command.Request.Permissions, Permissions.Exists);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (DutyConflicts.FindIn(created.Value.PermissionNames) is { } pair)
        {
            return RoleErrors.DutyConflict(pair);
        }

        if (await db.Set<Role>().AsNoTracking().AnyAsync(r => r.Code == created.Value.Code, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.CodeTaken;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        db.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetRoleQuery(created.Value.Id), cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<RoleSummary>> Handle(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.Id, command.Version,
            role => SetPermissionsAsync(role, command.Request.Name, command.Request.Description, command.Request.Permissions, cancellationToken),
            cancellationToken);
    }

    /// <summary>Restores the permissions a seeded role shipped with (<see cref="DefaultRoles"/>); name and description stay.</summary>
    public Task<Result<RoleSummary>> Handle(ResetRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.Id, command.Version, role =>
        {
            if (DefaultRoles.Bundles.TryGetValue(role.Code, out var shipped))
            {
                return SetPermissionsAsync(role, role.Name, role.Description, shipped, cancellationToken);
            }

            var refusal = role.IsLocked ? RoleErrors.Locked : RoleErrors.NoDefault;
            return Task.FromResult(Result.Failure(refusal));
        }, cancellationToken);
    }

    /// <summary>Clears the "changed by upgrade" mark once an administrator has seen what the upgrade granted (#211); audited.</summary>
    public Task<Result<RoleSummary>> Handle(ConfirmRoleUpgradeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.Id, command.Version, role => Task.FromResult(role.ConfirmUpgrade()), cancellationToken);
    }

    /// <summary>A new, editable role with the permissions and description of another; the Superadministrator role is not copied.</summary>
    public async Task<Result<RoleSummary>> Handle(CopyRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var source = await db.Set<Role>().AsNoTracking().Include(r => r.Permissions)
            .SingleOrDefaultAsync(r => r.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return RoleErrors.NotFound;
        }

        if (source.Code == AppRoles.Superadministrator)
        {
            return RoleErrors.SuperadministratorRole;
        }

        return await Handle(
            new CreateRoleCommand(new CreateRoleRequest(command.Request.Code, command.Request.Name, source.Description, [.. source.PermissionNames])),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<RoleSummary>> Handle(ArchiveRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.Id, command.Version, async role =>
        {
            if (await db.Set<PositionRole>().AsNoTracking().AnyAsync(r => r.Id == role.Id, cancellationToken).ConfigureAwait(false))
            {
                return RoleErrors.InUse;
            }

            return role.Archive();
        }, cancellationToken);
    }

    public Task<Result<RoleSummary>> Handle(RestoreRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ChangeAsync(command.Id, command.Version, role =>
        {
            role.Restore();
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    private async Task<Result> SetPermissionsAsync(
        Role role, string? name, string? description, IEnumerable<string> permissions, CancellationToken cancellationToken)
    {
        if (role.IsLocked)
        {
            return RoleErrors.Locked;
        }

        if (guard.HoldsRole(role.Code))
        {
            return RoleErrors.OwnRole;
        }

        var wanted = permissions.ToHashSet(StringComparer.Ordinal);
        if (await guard.ConflictForRoleAsync(role.Id, wanted, cancellationToken).ConfigureAwait(false) is { } pair)
        {
            return RoleErrors.DutyConflict(pair);
        }

        var changed = role.Update(name, description, wanted, Permissions.Exists);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        if (changed.Value)
        {
            await stamps.RotateForRoleAsync(role.Id, cancellationToken).ConfigureAwait(false);
        }

        // A seeded role away from its shipped set is customised: a later migration's grant to it is marked for review (#211).
        if (DefaultRoles.Bundles.TryGetValue(role.Code, out var shipped))
        {
            role.MarkCustomised(!shipped.SetEquals(role.PermissionNames));
        }

        return Result.Success();
    }

    private async Task<Result<RoleSummary>> ChangeAsync(short id, uint? version, Func<Role, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return VersionErrors.Required;
        }

        var role = await db.Set<Role>().Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        db.Entry(role).Property<uint>("xmin").OriginalValue = version.Value;
        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        var result = await change(role).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // A permission change writes only iam.role_permission; touching the role row checks If-Match and bumps the version.
        db.Entry(role).Property(r => r.Name).IsModified = true;
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetRoleQuery(id), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Roles and the permission catalogue under <c>/api/v1/identity</c> (ADR 0028).</summary>
internal static class RoleEndpoints
{
    private static readonly RoleSummary Example = new(
        7, "PURCHASING_CLERK", "Purchasing clerk", "Prepares purchase orders.", [Permissions.Procurement.PurchaseOrder.Create, Permissions.Inventory.Part.View],
        false, false, ["PURCHASER"], false, false, null, "8841");

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/permissions", () => Results.Ok(Permissions.All.Select(p => new PermissionDto(p.Name, p.Module, p.Description))))
            .RequireAuthorization(Permissions.Organisation.Role.View)
            .Documented("ListPermissions", "Lists the permission catalogue.",
                "Every permission of the code catalogue with its module and what it allows, for building roles. Permission Organisation.Role.View.")
            .Produces<IReadOnlyList<PermissionDto>>()
            .WithResponseExample(StatusCodes.Status200OK, new[] { new PermissionDto(Permissions.Inventory.Transfer.Approve, "Inventory", "Approve transfers of the brands one manages.") });

        var roles = v1.MapGroup("/roles").RequireAuthorization(Permissions.Organisation.Role.View).WithTags("Organisation");

        roles.MapGet("", async (IQueryHandler<ListRolesQuery, IReadOnlyList<RoleSummary>> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new ListRolesQuery(), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .Documented("ListRoles", "Lists the roles with their permissions.",
                "Every role by code: permissions, whether it is locked or archived, and the positions whose template holds it. Permission Organisation.Role.View.")
            .Produces<IReadOnlyList<RoleSummary>>()
            .WithResponseExample(StatusCodes.Status200OK, new[] { Example });

        roles.MapGet("/{id:int}", async ([Description("Role id.")] short id, IQueryHandler<GetRoleQuery, RoleSummary> handler, CancellationToken cancellationToken) =>
                OrganisationApi.WithETag(await handler.Handle(new GetRoleQuery(id), cancellationToken).ConfigureAwait(false), r => r.Version))
            .Documented("GetRole", "Returns one role.", "The role with its permissions; the ETag header carries its version for If-Match. Permission Organisation.Role.View.")
            .Produces<RoleSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status404NotFound, OrganisationApi.Problem(RoleErrors.NotFound, 404));

        roles.MapPost("", async (CreateRoleRequest request, ICommandHandler<CreateRoleCommand, RoleSummary> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreateRoleCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Created($"/api/v1/identity/roles/{result.Value.Id}", result.Value) : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Organisation.Role.Edit)
            .Documented("CreateRole", "Creates a role.",
                "A named bundle of permissions of the catalogue. A bundle that breaks separation of duties is refused (409 Identity.DutyConflict with the pair). "
                + "Permission Organisation.Role.Edit.")
            .Accepts<CreateRoleRequest>("application/json")
            .Produces<RoleSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CreateRoleRequest("PURCHASING_CLERK", "Purchasing clerk", "Prepares purchase orders.", [Permissions.Procurement.PurchaseOrder.Create]))
            .WithResponseExample(StatusCodes.Status201Created, Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(
                RoleErrors.DutyConflict((Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.Supplier.Edit)), 409));

        roles.MapPut("/{id:int}", async (
                [Description("Role id.")] short id, UpdateRoleRequest request,
                [Description("The version read, as its ETag.")][FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<UpdateRoleCommand, RoleSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new UpdateRoleCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Role.Edit)
            .Documented("UpdateRole", "Changes a role and its permissions.",
                "Needs If-Match. Every account whose position holds the role gets the new permissions within the session check (30 seconds). "
                + "Refused for a locked role (422 Identity.RoleLocked), for a role the caller holds (403 SS301), and when the role or a position "
                + "holding it would break separation of duties (409 Identity.DutyConflict). Permission Organisation.Role.Edit.")
            .Accepts<UpdateRoleRequest>("application/json")
            .Produces<RoleSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new UpdateRoleRequest("Purchasing clerk", null, [Permissions.Procurement.PurchaseOrder.Create, Permissions.Inventory.Part.View]))
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OrganisationApi.Problem(RoleErrors.Locked, 422));

        MapState<ArchiveRoleCommand>(roles, "archive", (id, v) => new ArchiveRoleCommand(id, v), "ArchiveRole", "Archives a role.",
            "Needs If-Match. Refused for a locked role (422) and while a position's template holds it (409 SS501). Permission Organisation.Role.Edit.");
        MapState<RestoreRoleCommand>(roles, "restore", (id, v) => new RestoreRoleCommand(id, v), "RestoreRole", "Restores an archived role.",
            "Needs If-Match. Permission Organisation.Role.Edit.");
        MapState<ConfirmRoleUpgradeCommand>(roles, "confirm-upgrade", (id, v) => new ConfirmRoleUpgradeCommand(id, v), "ConfirmRoleUpgrade",
            "Confirms the permissions an upgrade granted to a customised role.",
            "Needs If-Match. A migration that gives a new permission to a default role also grants it to the seeded role an administrator "
            + "changed, audits it as the system user and marks the role \"changed by upgrade\" (upgradePending). This clears the mark, audited. "
            + "422 Identity.NoUpgradePending when nothing waits. Permission Organisation.Role.Edit.");
        MapState<ResetRoleCommand>(roles, "reset", (id, v) => new ResetRoleCommand(id, v), "ResetRole", "Resets a seeded role to its default permissions.",
            "Needs If-Match. Restores the permission set the role shipped with; name and description stay. Audited, and the holders' sessions follow "
            + "within the session check. Refused for a role without a default (422 Identity.NoDefaultRole), a locked role (422), a role the caller "
            + "holds (403 SS301) and when a position holding it would break separation of duties (409). Permission Organisation.Role.Edit.");

        roles.MapPost("/{id:int}/copy", async (
                [Description("Id of the role to copy.")] short id, CopyRoleRequest request, ICommandHandler<CopyRoleCommand, RoleSummary> handler,
                CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CopyRoleCommand(id, request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Created($"/api/v1/identity/roles/{result.Value.Id}", result.Value) : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Organisation.Role.Edit)
            .Documented("CopyRole", "Copies a role.",
                "A new, editable role with the permissions and description of the given one, for example a copy of a locked or seeded role to adapt. "
                + "The code must be new (409); the Superadministrator role is not copied (422). Permission Organisation.Role.Edit.")
            .Accepts<CopyRoleRequest>("application/json")
            .Produces<RoleSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new CopyRoleRequest("SENIOR_PLANNER", "Senior planner"))
            .WithResponseExample(StatusCodes.Status201Created, Example);
    }

    private static void MapState<TCommand>(RouteGroupBuilder group, string action, Func<short, uint?, TCommand> create, string name, string summary, string description)
        where TCommand : ICommand<RoleSummary> =>
        group.MapPost($"/{{id:int}}/{action}", async (
                [Description("Role id.")] short id, [Description("The version read, as its ETag.")][FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<TCommand, RoleSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(create(id, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Role.Edit)
            .Documented(name, summary, description)
            .Produces<RoleSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(RoleErrors.InUse, 409));
}
