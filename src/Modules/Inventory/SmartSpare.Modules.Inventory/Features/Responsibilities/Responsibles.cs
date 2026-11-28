using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Responsibilities;

internal static class ResponsibilityErrors
{
    public static readonly Error OwnAssignments = Error.Forbidden(
        "SS301", "You cannot change your own brand or hub assignments: someone else must grant you rights.");

    public static readonly Error RecordNotFound = Error.NotFound("Inventory.RecordNotFound", "The brand, warehouse, manufacturer or part does not exist.");

    public static readonly Error UnknownRole = Error.Validation("Inventory.UnknownAssignmentRole", "An assignment role does not exist for this kind of record.");

    public static readonly Error UnknownEmployee = Error.BusinessRule("Inventory.UnknownEmployee", "An employee does not exist or has left the company.");
}

internal sealed record ListAssignmentRolesQuery(string? RecordKind) : IQuery<IReadOnlyList<AssignmentRoleDto>>;

internal sealed record ListResponsiblesQuery(string RecordKind, long RecordId) : IQuery<IReadOnlyList<ResponsibleDto>>;

internal sealed record ReplaceResponsiblesCommand(string RecordKind, long RecordId, ResponsiblesRequest Request) : ICommand<IReadOnlyList<ResponsibleDto>>;

internal sealed class ReplaceResponsiblesCommandValidator : AbstractValidator<ReplaceResponsiblesCommand>
{
    public ReplaceResponsiblesCommandValidator() => RuleFor(c => c.Request).SetValidator(new ResponsiblesRequestValidator());
}

/// <summary>
/// Replaces the responsible employees of a brand, warehouse or manufacturer (ADR 0026 decision 3, STAGE_7_PLAN 3.7). When a role
/// that grants scope changes, the full new rights-bearing set goes to Identity as <see cref="ResponsibilitiesChanged"/>
/// in the same transaction, so the employees' rights follow at the next session check. Permissions Inventory.Brand.ManageAssignments and Inventory.Warehouse.ManageAssignments: these
/// assignments give rights.
/// </summary>
internal sealed class ReplaceResponsiblesCommandHandler(
    InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, IEmployeeDirectory employees, ICurrentUser user,
    IQueryHandler<ListResponsiblesQuery, IReadOnlyList<ResponsibleDto>> reader)
    : ICommandHandler<ReplaceResponsiblesCommand, IReadOnlyList<ResponsibleDto>>
{
    public async Task<Result<IReadOnlyList<ResponsibleDto>>> Handle(ReplaceResponsiblesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var exists = command.RecordKind switch
        {
            RecordKinds.Brand => await db.Set<Domain.Catalog.Brand>().AsNoTracking().AnyAsync(b => b.Id == command.RecordId, cancellationToken).ConfigureAwait(false),
            RecordKinds.Manufacturer => await db.Set<Domain.Catalog.Manufacturer>().AsNoTracking().AnyAsync(m => m.Id == command.RecordId, cancellationToken).ConfigureAwait(false),
            RecordKinds.Part => await db.Set<Domain.Catalog.Part>().AsNoTracking().AnyAsync(p => p.Id == command.RecordId, cancellationToken).ConfigureAwait(false),
            _ => await db.Set<Warehouse>().AsNoTracking().AnyAsync(w => w.Id == command.RecordId, cancellationToken).ConfigureAwait(false),
        };
        if (!exists)
        {
            return ResponsibilityErrors.RecordNotFound;
        }

        // An archived brand gets no new assignments (SS500, product owner 28 November 2026); clearing them stays possible.
        if (command.RecordKind == RecordKinds.Brand && command.Request.Responsibles.Count > 0
            && await db.Set<Domain.Catalog.Brand>().AsNoTracking().AnyAsync(b => b.Id == command.RecordId && b.IsArchived, cancellationToken).ConfigureAwait(false))
        {
            return SmartSpare.SharedKernel.MasterData.CatalogErrors.Archived("brand");
        }

        var roles = await db.Set<RefAssignmentRole>().AsNoTracking().Where(r => r.RecordKind == command.RecordKind)
            .ToDictionaryAsync(r => r.Code, r => r.GrantsScope, cancellationToken).ConfigureAwait(false);
        var requested = command.Request.Responsibles.Select(e => new ResponsibleEmployee(e.EmployeeId, e.Role.Trim().ToUpperInvariant())).Distinct().ToList();
        if (requested.Any(r => !roles.ContainsKey(r.Role)))
        {
            return ResponsibilityErrors.UnknownRole;
        }

        var current = await CurrentAsync(command, cancellationToken).ConfigureAwait(false);
        var change = ResponsibilityRules.Replace([.. current.Select(r => new ResponsibleEmployee(r.EmployeeId, r.Role))], requested, role => roles[role]);
        // ADR 0028: nobody changes their own rights-bearing assignments (someone else must grant rights).
        if (change.RightsChanged && user.UserId is { } me)
        {
            var own = (await employees.FindAccountOwnersAsync([me], cancellationToken).ConfigureAwait(false)).Select(o => o.EmployeeId).ToHashSet();
            if (change.Added.Concat(change.Removed).Any(r => roles[r.Role] && own.Contains(r.EmployeeId)))
            {
                return ResponsibilityErrors.OwnAssignments;
            }
        }

        var added = change.Added.Select(a => a.EmployeeId).Distinct().ToList();
        if (added.Count > 0)
        {
            var known = (await employees.FindAsync(added, cancellationToken).ConfigureAwait(false)).Where(e => e.IsActive).Select(e => e.Id).ToHashSet();
            if (!added.All(known.Contains))
            {
                return ResponsibilityErrors.UnknownEmployee;
            }
        }

        foreach (var removed in current.Where(r => change.Removed.Contains(new ResponsibleEmployee(r.EmployeeId, r.Role))))
        {
            db.Remove(removed);
        }

        foreach (var add in change.Added)
        {
            db.Add(command.RecordKind switch
            {
                RecordKinds.Brand => new BrandResponsible((short)command.RecordId, add.EmployeeId, add.Role, user.UserId),
                RecordKinds.Manufacturer => new ManufacturerResponsible((short)command.RecordId, add.EmployeeId, add.Role, user.UserId),
                RecordKinds.Part => new PartResponsible(command.RecordId, add.EmployeeId, add.Role, user.UserId),
                _ => (object)new WarehouseResponsible((short)command.RecordId, add.EmployeeId, add.Role, user.UserId),
            });
        }

        if (change.RightsChanged)
        {
            unitOfWork.AddIntegrationEvent(new ResponsibilitiesChanged(
                command.RecordKind, (short)command.RecordId, [.. change.RightsBearing.Select(r => new ResponsibleAssignment(r.EmployeeId, r.Role))],
                user.UserId ?? Guid.Empty));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new ListResponsiblesQuery(command.RecordKind, command.RecordId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<IResponsibility>> CurrentAsync(ReplaceResponsiblesCommand command, CancellationToken cancellationToken) =>
        command.RecordKind switch
        {
            RecordKinds.Brand => [.. await db.Set<BrandResponsible>().Where(r => r.BrandId == command.RecordId).ToListAsync(cancellationToken).ConfigureAwait(false)],
            RecordKinds.Manufacturer => [.. await db.Set<ManufacturerResponsible>().Where(r => r.ManufacturerId == command.RecordId).ToListAsync(cancellationToken).ConfigureAwait(false)],
            RecordKinds.Part => [.. await db.Set<PartResponsible>().Where(r => r.PartId == command.RecordId).ToListAsync(cancellationToken).ConfigureAwait(false)],
            _ => [.. await db.Set<WarehouseResponsible>().Where(r => r.WarehouseId == command.RecordId).ToListAsync(cancellationToken).ConfigureAwait(false)],
        };
}

/// <summary>
/// GET and PUT of the responsible employees of brands and warehouses, the assignment roles and the brand list, under
/// <c>/api/v1/inventory</c>. Reads: Inventory.Brand.View or Inventory.Warehouse.View; changes: Inventory.Brand.ManageAssignments or
/// Inventory.Warehouse.ManageAssignments (the assignments give rights, ADR 0026, ADR 0028).
/// </summary>
internal static class ResponsibleEndpoints
{
    private static readonly ResponsibleDto Example = new(
        Guid.Parse("00000000-0000-4000-8000-000000000006"), "E0006", "Eglė Stankevičiūtė", true, "KEEPER", "Keeper", true);

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/assignment-roles", async (
                IQueryHandler<ListAssignmentRolesQuery, IReadOnlyList<AssignmentRoleDto>> handler, CancellationToken cancellationToken,
                [Description("brand, warehouse or manufacturer; empty for every kind.")] string? kind = null) =>
                (await handler.Handle(new ListAssignmentRolesQuery(kind), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Brand.View)
            .WithName("ListAssignmentRoles")
            .WithSummary("Lists the assignment roles of responsible employees.")
            .WithDescription("Roles per record kind; roles that grant scope give brand approval scope or hub access (ADR 0026). Permission Inventory.Brand.View.")
            .Produces<IReadOnlyList<AssignmentRoleDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { new AssignmentRoleDto("warehouse", "KEEPER", "Keeper", true) });

        // Rights-bearing assignments (brands, warehouses) need the ManageAssignments permission; a manufacturer's and a
        // part's roles give no rights, so the catalogue's edit permission is enough (STAGE_7_PLAN 3.7).
        foreach (var (segment, kind, view, manage, idType) in new[]
        {
            ("brands", RecordKinds.Brand, Permissions.Inventory.Brand.View, Permissions.Inventory.Brand.ManageAssignments, "int"),
            ("warehouses", RecordKinds.Warehouse, Permissions.Inventory.Warehouse.View, Permissions.Inventory.Warehouse.ManageAssignments, "int"),
            ("manufacturers", RecordKinds.Manufacturer, Permissions.Inventory.Manufacturer.View, Permissions.Inventory.Manufacturer.Edit, "int"),
            ("parts", RecordKinds.Part, Permissions.Inventory.Part.View, Permissions.Inventory.Part.Edit, "long"),
        })
        {
            v1.MapGet($"/{segment}/{{id:{idType}}}/responsibles", async (
                    [Description("Brand, warehouse, manufacturer or part id.")] long id, IQueryHandler<ListResponsiblesQuery, IReadOnlyList<ResponsibleDto>> handler,
                    CancellationToken cancellationToken) =>
                    (await handler.Handle(new ListResponsiblesQuery(kind, id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
                .RequireAuthorization(view)
                .WithName($"List{Pascal(kind)}Responsibles")
                .WithSummary($"Lists the responsible employees of a {kind}.")
                .WithDescription($"Employees with their assignment role on the {kind}, by name; an employee who left is marked. Permission {view}.")
                .Produces<IReadOnlyList<ResponsibleDto>>()
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .WithResponseExample(StatusCodes.Status200OK, new[] { Example });

            v1.MapPut($"/{segment}/{{id:{idType}}}/responsibles", async (
                    [Description("Brand, warehouse, manufacturer or part id.")] long id, ResponsiblesRequest request,
                    ICommandHandler<ReplaceResponsiblesCommand, IReadOnlyList<ResponsibleDto>> handler, CancellationToken cancellationToken) =>
                    (await handler.Handle(new ReplaceResponsiblesCommand(kind, id, request), cancellationToken).ConfigureAwait(false)).ToHttpResult())
                .RequireAuthorization(manage)
                .WithName($"Replace{Pascal(kind)}Responsibles")
                .WithSummary($"Replaces the responsible employees of a {kind}.")
                .WithDescription(
                    $"The complete new set of employee and role pairs of the {kind}. A change of a role that grants scope (brand manager, keeper, "
                    + "service base) changes the employees' rights: Identity receives it as an event and their sessions follow at the next "
                    + $"session check (at most 30 seconds). New employees must be active (422 Inventory.UnknownEmployee). Permission {manage}.")
                .Accepts<ResponsiblesRequest>("application/json")
                .Produces<IReadOnlyList<ResponsibleDto>>()
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .WithRequestExample(new ResponsiblesRequest([new ResponsibleEntry(Example.EmployeeId, "KEEPER")]))
                .WithResponseExample(StatusCodes.Status200OK, new[] { Example })
                .WithResponseExample(StatusCodes.Status422UnprocessableEntity,
                    OpenApiConventions.Problem(422, "Business rule violated", ResponsibilityErrors.UnknownEmployee.Code, ResponsibilityErrors.UnknownEmployee.Description));
        }
    }

    private static string Pascal(string kind) => char.ToUpperInvariant(kind[0]) + kind[1..];
}
