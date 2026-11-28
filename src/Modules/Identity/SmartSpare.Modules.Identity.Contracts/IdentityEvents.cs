using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// An employee as other modules copy it (ADR 0029 amendment 2): id, number, display name, whether the employee still
/// works here (not left, not anonymised), and the account the employee owns, if any.
/// </summary>
public sealed record EmployeeSnapshot(Guid EmployeeId, string Code, string DisplayName, bool IsActive, Guid? AccountId);

/// <summary>
/// Employees were created or changed (ADR 0029 amendment 2, PR 7j1c): the current state of every employee a write
/// touched, so Inventory's <c>inventory.employee_snapshot</c> keeps the newest state per employee for responsible
/// assignments and the names in record history. Written in the same transaction as the change: create, edit, leave,
/// restore, account creation and anonymisation, which also redacts the name in earlier payloads of this event;
/// <c>identity.employee-publish</c> sends every employee, in chunks of at most <see cref="MaxEmployees"/>.
/// </summary>
public sealed record EmployeeDirectoryChanged(IReadOnlyList<EmployeeSnapshot> Employees) : IntegrationEvent
{
    public const int MaxEmployees = 500;
}
