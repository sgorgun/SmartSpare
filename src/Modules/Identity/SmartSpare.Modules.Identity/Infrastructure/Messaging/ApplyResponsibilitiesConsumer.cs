using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Identity.Infrastructure.Messaging;

/// <summary>
/// Applies <see cref="ResponsibilitiesChanged"/> to Identity's projection (ADR 0026 decision 3, STAGE_7_PLAN 1.3): the
/// event carries the full rights-bearing set of one record, so the consumer replaces that record's rows when the event is
/// newer than the last one applied (state transfer: idempotent and order-safe), and rotates the security stamps of every
/// account whose employee gained or lost an assignment, so the claims follow at the next session check. Runs in Identity's
/// transaction with the inbox row; acts as the user who made the change, for attribution only (ADR 0024).
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ApplyResponsibilitiesConsumer(IdentityDbContext db) : IIntegrationEventConsumer<ResponsibilitiesChanged>
{
    public const string Name = "identity.apply-responsibilities";

    public async Task Handle(ResponsibilitiesChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        NpgsqlParameter[] Record() =>
        [
            new("kind", integrationEvent.RecordKind), new("record", integrationEvent.RecordId), new("occurred", integrationEvent.OccurredAt),
        ];

        var newer = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT NOT EXISTS (SELECT 1 FROM iam.responsibility_source
                                   WHERE record_kind = @kind AND record_id = @record AND source_occurred_at >= @occurred) AS "Value"
                """,
                Record())
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!newer)
        {
            return;
        }

        var assignments = integrationEvent.Assignments;
        var previous = await db.Database.SqlQueryRaw<Guid>(
                "SELECT employee_id AS \"Value\" FROM iam.employee_responsibility WHERE record_kind = @kind AND record_id = @record", Record())
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM iam.employee_responsibility WHERE record_kind = @kind AND record_id = @record", Record(), cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
            SELECT DISTINCT @kind, @record, a.employee_id, a.assignment_role
            FROM unnest(@employees, @roles) AS a (employee_id, assignment_role)
            JOIN iam.employee e ON e.id = a.employee_id
            """,
            [.. Record(), new NpgsqlParameter("employees", assignments.Select(a => a.EmployeeId).ToArray()),
             new NpgsqlParameter("roles", assignments.Select(a => a.Role).ToArray())],
            cancellationToken).ConfigureAwait(false);

        var affected = previous.Concat(assignments.Select(a => a.EmployeeId)).Distinct().ToArray();
        if (affected.Length > 0)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE iam.app_user SET security_stamp = gen_random_uuid() WHERE employee_id = ANY (@affected)",
                [new NpgsqlParameter("affected", affected)], cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO iam.responsibility_source (record_kind, record_id, source_occurred_at) VALUES (@kind, @record, @occurred)
            ON CONFLICT (record_kind, record_id) DO UPDATE SET source_occurred_at = EXCLUDED.source_occurred_at
            """,
            Record(), cancellationToken).ConfigureAwait(false);
    }
}
