using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Identity;

/// <summary>The organisation facts behind an account's rights: the employee's status, position and role template.</summary>
internal sealed record EmployeeRights(bool EmployeeActive, string PositionCode, string PositionName, string[] Roles);

/// <summary>
/// Reads what <see cref="EffectiveRightsCalculator"/> needs (ADR 0026) and builds the rights of an account. Until PR 7d
/// the brand scope and the hub still come from the account's own brand list and home hub; 7d replaces both with the
/// responsibility assignments of the brand and warehouse records.
/// </summary>
internal sealed class OrganisationReader(IdentityDbContext db)
{
    public async Task<EmployeeRights> EmployeeAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<EmployeeRights>(
                """
                SELECT (NOT e.is_archived AND e.anonymised_at IS NULL) AS employee_active,
                       p.code::text AS position_code, p.name::text AS position_name,
                       coalesce((SELECT array_agg(r.code::text ORDER BY r.code) FROM iam.position_role pr JOIN iam.role r ON r.id = pr.role_id
                                 WHERE pr.position_id = p.id), ARRAY[]::text[]) AS roles
                FROM iam.employee e JOIN iam.position p ON p.id = e.position_id
                WHERE e.id = @employee
                """,
                new NpgsqlParameter<Guid>("employee", employeeId))
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The rights of an account for its claims: brand and hub facts carry ids only.</summary>
    public async Task<EffectiveRights> RightsForClaimsAsync(AppUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var employee = await EmployeeAsync(user.EmployeeId, cancellationToken).ConfigureAwait(false);
        var brands = user.Brands.Select(b => new ScopeFact(b.BrandId, b.BrandId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Brand list of the account"));
        var hubs = user.HomeWarehouseId is { } hub
            ? [new ScopeFact(hub, hub.ToString(System.Globalization.CultureInfo.InvariantCulture), "Home hub of the account")]
            : Array.Empty<ScopeFact>();
        return EffectiveRightsCalculator.Calculate(new RightsInput(
            user.IsActive && user.AnonymisedAt is null, employee.EmployeeActive, employee.PositionCode, employee.PositionName,
            employee.Roles, [.. brands], hubs));
    }

    /// <summary>Whether an employee may use an account: not left and not anonymised.</summary>
    public Task<bool> EmployeeIsActiveAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Set<Employee>().AsNoTracking().AnyAsync(e => e.Id == employeeId && !e.IsArchived && e.AnonymisedAt == null, cancellationToken);
}

/// <summary>
/// Rotates the security stamps of accounts whose rights changed (ADR 0026, STAGE_7_PLAN 1.4), in the command's
/// transaction: their sessions are rebuilt or ended at the next session check.
/// </summary>
internal sealed class AccountStamps(IdentityDbContext db)
{
    /// <summary>Every account of an employee in the position (its role template changed).</summary>
    public Task<int> RotateForPositionAsync(short positionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE iam.app_user u SET security_stamp = gen_random_uuid() FROM iam.employee e WHERE e.id = u.employee_id AND e.position_id = {positionId}",
            cancellationToken);
}

/// <summary>
/// Keeps at least one active administrator (STAGE_7_PLAN 1.2): an active employee with an active account whose
/// position's template has ADMINISTRATOR.
/// </summary>
internal sealed class AdministratorGuard(IdentityDbContext db)
{
    /// <summary>Active administrators other than the given employee and outside the given position.</summary>
    public async Task<bool> AnotherAdministratorExistsAsync(Guid? exceptEmployee, short? exceptPosition, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM iam.employee e
                    JOIN iam.app_user u       ON u.employee_id = e.id AND u.is_active AND u.anonymised_at IS NULL
                    JOIN iam.position_role pr ON pr.position_id = e.position_id
                    JOIN iam.role r           ON r.id = pr.role_id AND r.code = 'ADMINISTRATOR'
                    WHERE NOT e.is_archived AND e.anonymised_at IS NULL
                      AND (@employee IS NULL OR e.id <> @employee)
                      AND (@position IS NULL OR e.position_id <> @position)) AS "Value"
                """,
                new NpgsqlParameter("employee", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)exceptEmployee ?? DBNull.Value },
                new NpgsqlParameter("position", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = (object?)exceptPosition ?? DBNull.Value })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the position's template grants ADMINISTRATOR.</summary>
    public Task<bool> GrantsAdministratorAsync(IReadOnlySet<short> roleIds, CancellationToken cancellationToken) =>
        db.Set<Role>().AsNoTracking().AnyAsync(r => roleIds.Contains(r.Id) && r.Code == "ADMINISTRATOR", cancellationToken);
}
