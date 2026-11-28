using Microsoft.EntityFrameworkCore;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Passes the reason a user gave for a change into the audit trail (<c>app.reason</c>, read by <c>audit.trg_row_audit</c>
/// into <c>audit.audit_log.reason</c>). It never invents a reason: the caller passes the user's own text, required by the
/// API contract (ADR 0027 decision 6). Transaction-local, so it must run inside the write's transaction.
/// </summary>
public static class AuditReason
{
    public static async Task SetAsync(DbContext db, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The audit reason is transaction-local; set it inside the write's transaction.");
        }

        await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.reason', {0}, true)", [reason.Trim()], cancellationToken).ConfigureAwait(false);
    }
}
