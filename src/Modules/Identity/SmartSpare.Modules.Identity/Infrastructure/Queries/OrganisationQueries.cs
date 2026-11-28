using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Features.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Infrastructure.Queries;

// Data access of the organisation (ADR 0026): positions and employees read PostgreSQL directly; search uses the trigram
// indexes on the names and the unique codes.

internal static class SearchPattern
{
    /// <summary>An escaped <c>ILIKE '%…%'</c> pattern, or <see langword="null"/> for no search.</summary>
    public static NpgsqlParameter For(string? search)
    {
        var text = search?.Trim();
        var pattern = string.IsNullOrEmpty(text)
            ? null
            : "%" + text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        return new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = (object?)pattern ?? DBNull.Value };
    }
}

internal sealed class PositionQueries(IdentityDbContext db)
    : IQueryHandler<ListPositionsQuery, PagedResponse<PositionSummary>>,
      IQueryHandler<GetPositionQuery, PositionSummary>
{
    private const string Select =
        """
        SELECT p.id, p.code::text AS code, p.name::text AS name, p.description::text AS description,
               coalesce((SELECT array_agg(r.code::text ORDER BY r.code) FROM iam.position_role pr JOIN iam.role r ON r.id = pr.role_id
                         WHERE pr.position_id = p.id), ARRAY[]::text[]) AS roles,
               p.is_locked, p.is_archived, (SELECT count(*)::int FROM iam.employee e WHERE e.position_id = p.id AND NOT e.is_archived) AS active_employees,
               p.xmin::text AS version
        FROM iam.position p
        """;

    private const string Filter = "WHERE p.is_archived = @archived AND (@pattern IS NULL OR p.code ILIKE @pattern OR p.name ILIKE @pattern)";

    public async Task<Result<PagedResponse<PositionSummary>>> Handle(ListPositionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var total = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM iam.position p " + Filter, SearchPattern.For(query.Search), Archived(query.Archived))
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Database.SqlQueryRaw<PositionRow>(
                Select + " " + Filter + " ORDER BY p.name LIMIT @limit OFFSET @offset",
                SearchPattern.For(query.Search), Archived(query.Archived),
                new NpgsqlParameter<int>("limit", query.PageSize), new NpgsqlParameter<int>("offset", (query.Page - 1) * query.PageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResponse<PositionSummary>([.. rows.Select(r => r.ToDto())], query.Page, query.PageSize, total);
    }

    public async Task<Result<PositionSummary>> Handle(GetPositionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var row = await db.Database.SqlQueryRaw<PositionRow>(Select + " WHERE p.id = @id", new NpgsqlParameter<short>("id", query.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? OrganisationErrors.PositionNotFound : row.ToDto();
    }

    private static NpgsqlParameter<bool> Archived(bool archived) => new("archived", archived);
}

internal sealed record PositionRow(short Id, string Code, string Name, string? Description, string[] Roles, bool IsLocked, bool IsArchived, int ActiveEmployees, string Version)
{
    public PositionSummary ToDto() => new(Id, Code, Name, Description, Roles, IsLocked, IsArchived, ActiveEmployees, Version);
}

internal sealed class EmployeeQueries(IdentityDbContext db)
    : IQueryHandler<ListEmployeesQuery, PagedResponse<EmployeeSummary>>,
      IQueryHandler<GetEmployeeQuery, EmployeeSummary>
{
    private const string Select =
        """
        SELECT e.id, e.code::text AS code, e.full_name::text AS full_name, e.email::text AS email, e.phone::text AS phone,
               e.position_id, p.code::text AS position_code, p.name::text AS position_name, e.hired_on, e.left_on, e.is_archived,
               e.anonymised_at IS NOT NULL AS is_anonymised, u.user_name::text AS account_user_name, e.xmin::text AS version
        FROM iam.employee e
        JOIN iam.position p ON p.id = e.position_id
        LEFT JOIN iam.app_user u ON u.employee_id = e.id
        """;

    private const string Filter =
        """
        WHERE e.is_archived = @archived AND (@position IS NULL OR e.position_id = @position)
          AND (@pattern IS NULL OR e.code ILIKE @pattern OR e.full_name ILIKE @pattern)
        """;

    public async Task<Result<PagedResponse<EmployeeSummary>>> Handle(ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        NpgsqlParameter[] Filters() =>
        [
            SearchPattern.For(query.Search), new NpgsqlParameter<bool>("archived", query.Archived),
            new NpgsqlParameter("position", NpgsqlDbType.Smallint) { Value = (object?)query.PositionId ?? DBNull.Value },
        ];

        var total = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM iam.employee e " + Filter, Filters())
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Database.SqlQueryRaw<EmployeeRow>(
                Select + " " + Filter + " ORDER BY e.full_name, e.code LIMIT @limit OFFSET @offset",
                [.. Filters(), new NpgsqlParameter<int>("limit", query.PageSize), new NpgsqlParameter<int>("offset", (query.Page - 1) * query.PageSize)])
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResponse<EmployeeSummary>([.. rows.Select(r => r.ToDto())], query.Page, query.PageSize, total);
    }

    public async Task<Result<EmployeeSummary>> Handle(GetEmployeeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var row = await db.Database.SqlQueryRaw<EmployeeRow>(Select + " WHERE e.id = @id", new NpgsqlParameter<Guid>("id", query.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? OrganisationErrors.EmployeeNotFound : row.ToDto();
    }
}

internal sealed record EmployeeRow(
    Guid Id, string Code, string FullName, string? Email, string? Phone, short PositionId, string PositionCode, string PositionName,
    DateOnly? HiredOn, DateOnly? LeftOn, bool IsArchived, bool IsAnonymised, string? AccountUserName, string Version)
{
    public EmployeeSummary ToDto() =>
        new(Id, Code, FullName, Email, Phone, PositionId, PositionCode, PositionName, HiredOn, LeftOn, IsArchived, IsAnonymised, AccountUserName, Version);
}
