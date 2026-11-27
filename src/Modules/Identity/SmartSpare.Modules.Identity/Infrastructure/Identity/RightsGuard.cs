using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>
/// The guardrails of ADR 0028 that need data: separation of duties over a role and over the union of a position's roles,
/// and the self-granting rule (nobody changes a role they hold, their own position, its template or their assignments).
/// </summary>
internal sealed class RightsGuard(IdentityDbContext db, ICurrentUser currentUser)
{
    /// <summary>The employee and position of the signed-in user, if the account belongs to one.</summary>
    public async Task<(Guid EmployeeId, short PositionId)?> CurrentEmployeeAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return null;
        }

        var row = await (from u in db.Set<AppUser>().AsNoTracking()
                         join e in db.Set<Employee>().AsNoTracking() on u.EmployeeId equals e.Id
                         where u.Id == userId
                         select new { e.Id, e.PositionId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : (row.Id, row.PositionId);
    }

    /// <summary>Whether the signed-in user holds the role (it is in the session's role codes).</summary>
    public bool HoldsRole(string roleCode) => currentUser.Roles.Contains(roleCode, StringComparer.Ordinal);

    /// <summary>The first conflicting pair in the union of the permissions of <paramref name="roleIds"/>, or none.</summary>
    public async Task<(string First, string Second)?> ConflictAsync(IEnumerable<short> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds.ToHashSet();
        var permissions = await db.Set<RolePermission>().AsNoTracking().Where(p => ids.Contains(p.RoleId)).Select(p => p.Permission)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return DutyConflicts.FindIn(permissions);
    }

    /// <summary>
    /// The first conflicting pair that a role's new permissions would create, in the role itself or in any position whose
    /// template holds it (with that position's other roles), or none.
    /// </summary>
    public async Task<(string First, string Second)?> ConflictForRoleAsync(short roleId, IReadOnlySet<string> newPermissions, CancellationToken cancellationToken)
    {
        if (DutyConflicts.FindIn(newPermissions) is { } own)
        {
            return own;
        }

        var positions = await db.Set<PositionRole>().AsNoTracking().Where(r => r.Id == roleId)
            .Select(r => EF.Property<short>(r, Persistence.Configurations.PositionConfiguration.PositionIdProperty))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var position in positions)
        {
            var others = await db.Set<PositionRole>().AsNoTracking()
                .Where(r => EF.Property<short>(r, Persistence.Configurations.PositionConfiguration.PositionIdProperty) == position && r.Id != roleId)
                .Join(db.Set<RolePermission>().AsNoTracking(), r => r.Id, p => p.RoleId, (_, p) => p.Permission)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (DutyConflicts.FindIn(others.Concat(newPermissions)) is { } pair)
            {
                return pair;
            }
        }

        return null;
    }
}
