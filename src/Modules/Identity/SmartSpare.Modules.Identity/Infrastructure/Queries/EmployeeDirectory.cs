using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

/// <summary>The in-process <see cref="IEmployeeDirectory"/> of the single host and of the Identity service (ADR 0026).</summary>
internal sealed class EmployeeDirectory(IdentityDbContext db) : IEmployeeDirectory
{
    public const int MaxLimit = 100;

    public async Task<IReadOnlyList<EmployeeInfo>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : await db.Set<Employee>().AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .OrderBy(e => e.FullName)
                .Select(e => new EmployeeInfo(e.Id, e.Code, e.FullName, !e.IsArchived && e.AnonymisedAt == null))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<AccountOwner>> FindAccountOwnersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        userIds.Count == 0
            ? []
            : await (from u in db.Set<AppUser>().AsNoTracking()
                     join e in db.Set<Employee>().AsNoTracking() on u.EmployeeId equals e.Id
                     where userIds.Contains(u.Id)
                     select new AccountOwner(u.Id, e.Id, e.FullName))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<EmployeeInfo>> SearchAsync(string? search, int limit, CancellationToken cancellationToken)
    {
        var text = search?.Trim();
        var query = db.Set<Employee>().AsNoTracking().Where(e => !e.IsArchived && e.AnonymisedAt == null);
        if (!string.IsNullOrEmpty(text))
        {
            var pattern = "%" + text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
            query = query.Where(e => EF.Functions.ILike(e.FullName, pattern) || EF.Functions.ILike(e.Code, pattern));
        }

        return await query.OrderBy(e => e.FullName).Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(e => new EmployeeInfo(e.Id, e.Code, e.FullName, true))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
