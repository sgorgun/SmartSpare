using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Modules.Fleet.Domain.Maintenance;

namespace SmartSpare.Modules.Fleet.Features.Maintenance;

/// <summary>
/// The due calculation as the API shows it (STAGE_6_PLAN sections 2.5 and 4.1): DTO mapping, the urgency order of
/// plans and the filters of the due list.
/// </summary>
internal static class MaintenanceDueMapping
{
    public static string StatusName(MaintenanceDueStatus status) => status switch
    {
        MaintenanceDueStatus.Overdue => MaintenanceDueStatuses.Overdue,
        MaintenanceDueStatus.DueSoon => MaintenanceDueStatuses.DueSoon,
        MaintenanceDueStatus.Planned => MaintenanceDueStatuses.Planned,
        _ => MaintenanceDueStatuses.Ok,
    };

    public static string TriggerName(MaintenanceDueTrigger trigger) =>
        trigger == MaintenanceDueTrigger.Calendar ? MaintenanceDueTriggers.Calendar : MaintenanceDueTriggers.EngineHours;

    public static MaintenancePlanDue ToDto(int planId, string planName, MaintenanceDueInput input, MaintenanceDue due)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(due);
        return new MaintenancePlanDue(
            planId, planName, input.IntervalHours, input.IntervalMonths, due.EffectiveInterval, due.DueEngineHours, due.RemainingHours,
            due.ProjectedDueDate, due.CalendarDueDate, due.DueDate, TriggerName(due.Trigger), StatusName(due.Status),
            input.OpenOrderThreshold);
    }

    /// <summary>Rank of a status: overdue first, then due soon, planned, ok.</summary>
    public static int Urgency(string status)
    {
        var index = MaintenanceDueStatuses.All.ToList().IndexOf(status);
        return index < 0 ? MaintenanceDueStatuses.All.Count : index;
    }

    /// <summary>Most urgent first: by status, then by due date (unknown dates last), then by remaining hours.</summary>
    public static IOrderedEnumerable<T> ByUrgency<T>(IEnumerable<T> items, Func<T, MaintenancePlanDue> due) =>
        items.OrderBy(i => Urgency(due(i).Status))
            .ThenBy(i => due(i).DueDate ?? DateOnly.MaxValue)
            .ThenBy(i => due(i).RemainingHours);

    /// <summary>The due list order: by due date (unknown dates last), then by remaining hours, then by urgency.</summary>
    public static IOrderedEnumerable<T> ByDueDate<T>(IEnumerable<T> items, Func<T, MaintenancePlanDue> due) =>
        items.OrderBy(i => due(i).DueDate ?? DateOnly.MaxValue)
            .ThenBy(i => due(i).RemainingHours)
            .ThenBy(i => Urgency(due(i).Status));

    /// <summary>The statuses a due list shows: the requested one, or overdue and due soon by default.</summary>
    public static IReadOnlySet<string> ListedStatuses(string? status) =>
        status is null
            ? new HashSet<string>(StringComparer.Ordinal) { MaintenanceDueStatuses.Overdue, MaintenanceDueStatuses.DueSoon }
            : new HashSet<string>(StringComparer.Ordinal) { status };
}
