using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

/// <summary>An employee as Inventory's copy holds it: id, number, display name, and whether the employee still works here.</summary>
internal sealed record EmployeeInfo(Guid Id, string Code, string FullName, bool IsActive);

/// <summary>
/// Inventory's read-only copy of Identity's employees (ADR 0029 amendment 2, PR 7j1c), in place of the synchronous call
/// to Identity: responsible assignments, the no-self-granting check and the names in record history.
/// </summary>
internal sealed class EmployeeCopy(InventoryDbContext db) : IActorNames
{
    /// <summary>The copied employees with these ids; unknown ids are left out.</summary>
    public async Task<IReadOnlyList<EmployeeInfo>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.Set<EmployeeSnapshot>().AsNoTracking().Where(e => ids.Contains(e.EmployeeId))
                .Select(e => new EmployeeInfo(e.EmployeeId, e.Code, e.DisplayName, e.IsActive))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The employee who owns the account (an <c>iam.app_user</c> id), if the copy knows it.</summary>
    public Task<Guid?> EmployeeOfAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Set<EmployeeSnapshot>().AsNoTracking().Where(e => e.AccountId == accountId).Select(e => (Guid?)e.EmployeeId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>"Who changed it" in a record's history: the display names behind the audit trail's account ids.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> FullNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<EmployeeSnapshot>().AsNoTracking().Where(e => e.AccountId != null && userIds.Contains(e.AccountId.Value))
                .ToDictionaryAsync(e => e.AccountId!.Value, e => e.DisplayName, cancellationToken).ConfigureAwait(false);
}
