using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

/// <summary>The in-process <see cref="IRolePermissions"/>: every role's bundle from <c>iam.role_permission</c>.</summary>
internal sealed class RolePermissionReader(IdentityDbContext db) : IRolePermissions
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken cancellationToken)
    {
        var rows = await (from rp in db.Set<RolePermission>().AsNoTracking()
                          join r in db.Set<Role>().AsNoTracking() on rp.RoleId equals r.Id
                          select new { r.Code, rp.Permission })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.Permission).Order(StringComparer.Ordinal)], StringComparer.Ordinal);
    }
}
