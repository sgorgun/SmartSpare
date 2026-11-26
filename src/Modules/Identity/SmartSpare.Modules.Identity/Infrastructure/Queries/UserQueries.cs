using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Features.Users;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

// Data access of user administration (#46): the masked directory and the form options read PostgreSQL directly. Features
// keep the queries, validation and rules; the unit metric of business logic covers Domain and Features (#63).

/// <summary>The masked user directory (<c>iam.v_app_user_masked</c>) with lockout and brands, by user name.</summary>
internal sealed class ListUsersQueryHandler(IdentityDbContext db) : IQueryHandler<ListUsersQuery, PagedResponse<UserSummary>>
{
    private const string FromClause =
        """
        FROM iam.v_app_user_masked v
        JOIN iam.app_user u ON u.id = v.id
        WHERE (@pattern IS NULL OR v.user_name ILIKE @pattern)
        """;

    private const string PageSql =
        """
        SELECT v.id, v.user_name, v.full_name_masked, v.email_masked, v.is_active,
               coalesce(u.lockout_end > now(), false) AS is_locked_out, v.is_anonymised, v.home_warehouse_id,
               coalesce(v.roles::text[], ARRAY[]::text[]) AS roles,
               coalesce((SELECT array_agg(b.code::text ORDER BY b.code) FROM iam.user_brand ub JOIN ref.brand b ON b.id = ub.brand_id
                         WHERE ub.user_id = v.id), ARRAY[]::text[]) AS brands,
               v.last_login_at

        """ + FromClause + "\nORDER BY v.user_name\nLIMIT @limit OFFSET @offset";

    public async Task<Result<PagedResponse<UserSummary>>> Handle(ListUsersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var search = query.Search?.Trim();
        var pattern = string.IsNullOrEmpty(search)
            ? null
            : "%" + search.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        NpgsqlParameter Pattern() => new("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value };

        var total = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\"\n" + FromClause, Pattern())
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Database
            .SqlQueryRaw<UserRow>(
                PageSql,
                Pattern(), new NpgsqlParameter<int>("limit", query.PageSize), new NpgsqlParameter<int>("offset", (query.Page - 1) * query.PageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<UserSummary>([.. rows.Select(r => r.ToDto())], query.Page, query.PageSize, total));
    }
}

internal sealed record UserRow(
    Guid Id,
    string UserName,
    string? FullNameMasked,
    string? EmailMasked,
    bool IsActive,
    bool IsLockedOut,
    bool IsAnonymised,
    short? HomeWarehouseId,
    string[] Roles,
    string[] Brands,
    DateTimeOffset? LastLoginAt)
{
    public UserSummary ToDto() => new(
        Id, UserName, FullNameMasked, EmailMasked, IsActive, IsLockedOut, IsAnonymised, HomeWarehouseId, Roles, Brands, LastLoginAt);
}

internal sealed class GetUserFormOptionsQueryHandler(IdentityDbContext db, IWarehouseQueries warehouses) : IQueryHandler<GetUserFormOptionsQuery, UserFormOptions>
{
    public async Task<Result<UserFormOptions>> Handle(GetUserFormOptionsQuery query, CancellationToken cancellationToken)
    {
        var roles = await db.Set<Role>().AsNoTracking().OrderBy(r => r.Code).Select(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var brands = await db.Database.SqlQueryRaw<string>("SELECT code::text AS \"Value\" FROM ref.brand ORDER BY code")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hubs = (await warehouses.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(w => w.IsActive).Select(w => new HubOption(w.Id, w.Code, w.Name)).ToList();
        return new UserFormOptions(roles, brands, hubs);
    }
}
