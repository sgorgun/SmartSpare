using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Features.Users;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

// Data access of user administration (#46): the masked directory and the form options read PostgreSQL directly. Features
// keep the queries, validation and rules; the unit metric of business logic covers Domain and Features (#63).

/// <summary>
/// The masked user directory (<c>iam.v_app_user_masked</c>) with lockout, the employee and position, and every effective
/// right with its origin (<see cref="EffectiveRightsCalculator"/>, ADR 0026), by user name.
/// </summary>
internal sealed class ListUsersQueryHandler(IdentityDbContext db, IWarehouseQueries warehouses) : IQueryHandler<ListUsersQuery, PagedResponse<UserSummary>>
{
    private const string FromClause =
        """
        FROM iam.v_app_user_masked v
        JOIN iam.app_user u ON u.id = v.id
        JOIN iam.employee e ON e.id = u.employee_id
        JOIN iam.position p ON p.id = e.position_id
        WHERE (@pattern IS NULL OR v.user_name ILIKE @pattern)
        """;

    private const string PageSql =
        """
        SELECT v.id, v.user_name, v.full_name_masked, v.email_masked, v.is_active,
               coalesce(u.lockout_end > now(), false) AS is_locked_out, v.is_anonymised, v.home_warehouse_id,
               coalesce(v.roles::text[], ARRAY[]::text[]) AS roles,
               coalesce((SELECT array_agg(b.code::text ORDER BY b.code) FROM iam.user_brand ub JOIN ref.brand b ON b.id = ub.brand_id
                         WHERE ub.user_id = v.id), ARRAY[]::text[]) AS brands,
               coalesce((SELECT array_agg(b.id ORDER BY b.code) FROM iam.user_brand ub JOIN ref.brand b ON b.id = ub.brand_id
                         WHERE ub.user_id = v.id), ARRAY[]::smallint[]) AS brand_ids,
               v.last_login_at, e.id AS employee_id, e.code::text AS employee_code, p.name::text AS position_name, p.code::text AS position_code,
               (NOT e.is_archived AND e.anonymised_at IS NULL) AS employee_active

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
        var hubs = (await warehouses.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(w => w.Id, w => w.Code);
        return Result.Success(new PagedResponse<UserSummary>([.. rows.Select(r => r.ToDto(hubs))], query.Page, query.PageSize, total));
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
    short[] BrandIds,
    DateTimeOffset? LastLoginAt,
    Guid EmployeeId,
    string EmployeeCode,
    string PositionName,
    string PositionCode,
    bool EmployeeActive)
{
    public UserSummary ToDto(IReadOnlyDictionary<short, string> hubCodes)
    {
        var brands = BrandIds.Zip(Brands, (id, code) => new ScopeFact(id, code, $"Brand list of the account ({code})"));
        var hubs = HomeWarehouseId is { } hub
            ? [new ScopeFact(hub, hubCodes.GetValueOrDefault(hub, $"#{hub}"), "Home hub of the account")]
            : Array.Empty<ScopeFact>();
        var rights = EffectiveRightsCalculator.Calculate(new RightsInput(
            IsActive && !IsAnonymised, EmployeeActive, PositionCode, PositionName, Roles, [.. brands], hubs));
        return new UserSummary(
            Id, UserName, FullNameMasked, EmailMasked, IsActive, IsLockedOut, IsAnonymised, HomeWarehouseId,
            [.. rights.Rights.Where(r => r.Kind == RightKind.Role).Select(r => r.Value)],
            [.. rights.Rights.Where(r => r.Kind == RightKind.Brand).Select(r => r.Value)],
            LastLoginAt, EmployeeId, EmployeeCode, PositionName,
            [.. rights.Rights.Select(ToDto)], [.. rights.WithoutEffect.Select(ToDto)], rights.InactiveReason);
    }

    private static EffectiveRightDto ToDto(EffectiveRight right) =>
        new(right.Kind.ToString(), right.Value, right.Origin.ToString(), right.OriginName);
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
