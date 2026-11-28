using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Messaging;

/// <summary>
/// Keeps <c>inventory.employee_snapshot</c> in step with Identity's employees (ADR 0029 amendment 2, PR 7j1c). Runs in
/// Inventory's transaction with the inbox row, so a redelivered event is skipped; <c>inventory.apply_employees</c>, the
/// copy's only write path, keeps the row of the newer event per employee.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed class EmployeeDirectoryChangedConsumer(InventoryDbContext db) : IIntegrationEventConsumer<EmployeeDirectoryChanged>
{
    public const string Name = "inventory.employee-snapshot";

    public Task Handle(EmployeeDirectoryChanged integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return integrationEvent.Employees.Count == 0
            ? Task.CompletedTask
            : db.Database.ExecuteSqlRawAsync("SELECT inventory.apply_employees(@occurred_at, @ids, @codes, @names, @active, @accounts)",
            [
                new NpgsqlParameter<Guid[]>("ids", [.. integrationEvent.Employees.Select(e => e.EmployeeId)]),
                new NpgsqlParameter<string[]>("codes", [.. integrationEvent.Employees.Select(e => e.Code)]),
                new NpgsqlParameter<string[]>("names", [.. integrationEvent.Employees.Select(e => e.DisplayName)]),
                new NpgsqlParameter<bool[]>("active", [.. integrationEvent.Employees.Select(e => e.IsActive)]),
                new NpgsqlParameter<Guid?[]>("accounts", [.. integrationEvent.Employees.Select(e => e.AccountId)]),
                new NpgsqlParameter<DateTimeOffset>("occurred_at", integrationEvent.OccurredAt),
            ],
            cancellationToken);
    }
}
