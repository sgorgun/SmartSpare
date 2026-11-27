using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Identity.Infrastructure.Messaging;

/// <summary>
/// Applies <see cref="ResponsibilitiesChanged"/> to Identity's projection (ADR 0026 decision 3, STAGE_7_PLAN 1.3) through
/// <c>iam.apply_responsibilities</c>, the projection's only write path: the event carries the full rights-bearing set of
/// one record, so the function replaces that record's rows when the event is newer than the last one applied (state
/// transfer: idempotent and order-safe) and rotates the security stamps of every account whose employee gained or lost an
/// assignment, so the claims follow at the next session check. Runs in Identity's transaction with the inbox row; acts as
/// the user who made the change, for attribution only (ADR 0024).
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class ApplyResponsibilitiesConsumer(IdentityDbContext db) : IIntegrationEventConsumer<ResponsibilitiesChanged>
{
    public const string Name = "identity.apply-responsibilities";

    public Task Handle(ResponsibilitiesChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return db.Database.ExecuteSqlRawAsync(
            "SELECT iam.apply_responsibilities(@kind, @record, @occurred, @employees, @roles)",
            [
                new NpgsqlParameter("kind", integrationEvent.RecordKind),
                new NpgsqlParameter("record", integrationEvent.RecordId),
                new NpgsqlParameter("occurred", integrationEvent.OccurredAt),
                new NpgsqlParameter("employees", integrationEvent.Assignments.Select(a => a.EmployeeId).ToArray()),
                new NpgsqlParameter("roles", integrationEvent.Assignments.Select(a => a.Role).ToArray()),
            ],
            cancellationToken);
    }
}
