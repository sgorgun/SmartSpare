using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

// The direct database work of user administration (#46). Features decide what may change and call these in the
// command's transaction; an architecture test keeps raw SQL out of Features.

/// <summary>
/// Administrative writes run as <c>app_admin</c> (#46): the API login may switch to the role for the current transaction
/// only (<c>SET LOCAL ROLE</c>, membership with INHERIT FALSE), so its everyday privileges stay those of <c>app_write</c>.
/// Per-module roles replace this in stage 10 (#16).
/// </summary>
internal sealed class AdminSession(IdentityDbContext db)
{
    public Task EnterAsync(CancellationToken cancellationToken) => db.Database.ExecuteSqlRawAsync("SET LOCAL ROLE app_admin", cancellationToken);
}

/// <summary>The SQL of user administration that EF Core does not express.</summary>
internal sealed class IdentityFunctions(IdentityDbContext db)
{
    /// <summary>Brand ids of the given codes (<c>ref.brand</c>, Platform reference data); unknown codes are left out.</summary>
    public async Task<IReadOnlyList<short>> BrandIdsAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        codes.Count == 0
            ? []
            : await db.Database.SqlQueryRaw<short>(
                    "SELECT id AS \"Value\" FROM ref.brand WHERE code = ANY(@codes)", new NpgsqlParameter<string[]>("codes", [.. codes]))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>GDPR erasure of an employee and the account through <c>iam.anonymise_employee</c> (role check and self-protection in SQL, SS301).</summary>
    public Task AnonymiseEmployeeAsync(Guid employeeId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"SELECT iam.anonymise_employee({employeeId})", cancellationToken);
}
