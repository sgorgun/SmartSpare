using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;
using SmartSpare.Modules.Inventory.Features.Responsibilities;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

/// <summary>The read side of responsible employees (ADR 0026): roles and a record's responsibles with names.</summary>
internal sealed class ResponsibleQueries(InventoryDbContext db, EmployeeCopy employees)
    : IQueryHandler<ListAssignmentRolesQuery, IReadOnlyList<AssignmentRoleDto>>,
      IQueryHandler<ListResponsiblesQuery, IReadOnlyList<ResponsibleDto>>
{
    public async Task<Result<IReadOnlyList<AssignmentRoleDto>>> Handle(ListAssignmentRolesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await db.Set<RefAssignmentRole>().AsNoTracking()
            .Where(r => query.RecordKind == null || r.RecordKind == query.RecordKind)
            .OrderBy(r => r.RecordKind).ThenByDescending(r => r.GrantsScope).ThenBy(r => r.Code)
            .Select(r => new AssignmentRoleDto(r.RecordKind, r.Code, r.Name, r.GrantsScope))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<ResponsibleDto>>> Handle(ListResponsiblesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<(Guid EmployeeId, string Role)> rows = query.RecordKind switch
        {
            RecordKinds.Brand => [.. (await db.Set<BrandResponsible>().AsNoTracking().Where(r => r.BrandId == query.RecordId)
                .Select(r => new { r.EmployeeId, r.Role }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => (r.EmployeeId, r.Role))],
            RecordKinds.Part => [.. (await db.Set<PartResponsible>().AsNoTracking().Where(r => r.PartId == query.RecordId)
                .Select(r => new { r.EmployeeId, r.Role }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => (r.EmployeeId, r.Role))],
            RecordKinds.Manufacturer => [.. (await db.Set<ManufacturerResponsible>().AsNoTracking().Where(r => r.ManufacturerId == query.RecordId)
                .Select(r => new { r.EmployeeId, r.Role }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => (r.EmployeeId, r.Role))],
            _ => [.. (await db.Set<WarehouseResponsible>().AsNoTracking().Where(r => r.WarehouseId == query.RecordId)
                .Select(r => new { r.EmployeeId, r.Role }).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => (r.EmployeeId, r.Role))],
        };
        var roles = await db.Set<RefAssignmentRole>().AsNoTracking().Where(r => r.RecordKind == query.RecordKind)
            .ToDictionaryAsync(r => r.Code, cancellationToken).ConfigureAwait(false);
        var people = (await employees.FindAsync([.. rows.Select(r => r.EmployeeId).Distinct()], cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        return rows
            .Select(r =>
            {
                var person = people.GetValueOrDefault(r.EmployeeId);
                var role = roles[r.Role];
                return new ResponsibleDto(r.EmployeeId, person?.Code ?? "?", person?.FullName ?? "Unknown employee", person?.IsActive ?? false,
                    r.Role, role.Name, role.GrantsScope);
            })
            .OrderBy(r => r.EmployeeName, StringComparer.CurrentCulture).ThenBy(r => r.Role, StringComparer.Ordinal)
            .ToList();
    }
}
