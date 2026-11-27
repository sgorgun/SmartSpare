using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Features.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

/// <summary>Roles with their permissions and the positions whose template holds them (ADR 0028).</summary>
internal sealed class RoleQueries(IdentityDbContext db) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleSummary>>, IQueryHandler<GetRoleQuery, RoleSummary>
{
    public async Task<Result<IReadOnlyList<RoleSummary>>> Handle(ListRolesQuery query, CancellationToken cancellationToken) =>
        await ReadAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task<Result<RoleSummary>> Handle(GetRoleQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roles = await ReadAsync(query.Id, cancellationToken).ConfigureAwait(false);
        return roles.Count == 1 ? roles[0] : RoleErrors.NotFound;
    }

    private async Task<List<RoleSummary>> ReadAsync(short? id, CancellationToken cancellationToken)
    {
        var roles = await db.Set<Role>().AsNoTracking().Include(r => r.Permissions)
            .Where(r => id == null || r.Id == id).OrderBy(r => r.Code)
            .Select(r => new { Role = r, Version = EF.Property<uint>(r, "xmin") })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var holders = await (from pr in db.Set<PositionRole>().AsNoTracking()
                             join p in db.Set<Position>().AsNoTracking() on EF.Property<short>(pr, PositionConfiguration.PositionIdProperty) equals p.Id
                             select new { RoleId = pr.Id, p.Code })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. roles.Select(r => new RoleSummary(
                r.Role.Id, r.Role.Code, r.Role.Name, r.Role.Description,
                [.. r.Role.PermissionNames.Order(StringComparer.Ordinal)], r.Role.IsLocked, r.Role.IsArchived,
                [.. holders.Where(h => h.RoleId == r.Role.Id).Select(h => h.Code).Order(StringComparer.Ordinal)],
                Versions.Format(r.Version))),
        ];
    }
}

