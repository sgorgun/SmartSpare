using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;

namespace SmartSpare.Modules.Fleet.Features.ServiceOrders;

/// <summary>Service order statuses and kit statuses between the database, the domain and the API.</summary>
internal static class ServiceOrderMapping
{
    private static readonly Dictionary<string, ServiceOrderStatus> SqlStatuses = new(StringComparer.Ordinal)
    {
        ["draft"] = ServiceOrderStatus.Draft,
        ["parts_reserved"] = ServiceOrderStatus.PartsReserved,
        ["in_progress"] = ServiceOrderStatus.InProgress,
        ["completed"] = ServiceOrderStatus.Completed,
        ["cancelled"] = ServiceOrderStatus.Cancelled,
    };

    public static string ApiStatus(ServiceOrderStatus status) => status switch
    {
        ServiceOrderStatus.PartsReserved => ServiceOrderStatuses.PartsReserved,
        ServiceOrderStatus.InProgress => ServiceOrderStatuses.InProgress,
        ServiceOrderStatus.Completed => ServiceOrderStatuses.Completed,
        ServiceOrderStatus.Cancelled => ServiceOrderStatuses.Cancelled,
        _ => ServiceOrderStatuses.Draft,
    };

    /// <summary><c>parts_reserved</c> (database) → <c>partsReserved</c> (API).</summary>
    public static string ApiStatus(string sqlStatus) => ApiStatus(SqlStatuses[sqlStatus]);

    /// <summary><c>partsReserved</c> (API) → <c>parts_reserved</c> (database); null stays null.</summary>
    public static string? SqlStatus(string? apiStatus) =>
        apiStatus is null ? null : SqlStatuses.Single(s => ApiStatus(s.Value) == apiStatus).Key;

    public static KitStatus KitOf(string sqlStatus, DateTimeOffset? kitReservedAt, int lines, int shortLines, int heldLines) =>
        ServiceOrder.KitOf(SqlStatuses[sqlStatus], kitReservedAt, lines, shortLines, heldLines);

    public static string KitStatusName(KitStatus kit) => kit switch
    {
        KitStatus.Reserving => KitStatuses.Reserving,
        KitStatus.Reserved => KitStatuses.Reserved,
        KitStatus.Partial => KitStatuses.Partial,
        KitStatus.NotHeld => KitStatuses.NotHeld,
        KitStatus.Issued => KitStatuses.Issued,
        KitStatus.Released => KitStatuses.Released,
        _ => KitStatuses.NoParts,
    };
}

/// <summary>
/// What the current user may do with a service order: the policy of each action (STAGE_6_PLAN 4.2) and the aggregate's
/// state rules, so a screen never offers an action the API would refuse.
/// </summary>
internal sealed class ServiceOrderAccess(ICurrentUser user)
{
    public IReadOnlyList<string> AllowedActions(ServiceOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var actions = new List<string>();
        if (May(AppPolicies.RecordServiceWork) && order.CanStart().IsSuccess)
        {
            actions.Add(ServiceOrderActions.Start);
        }

        if (May(AppPolicies.RecordServiceWork) && order.CanComplete().IsSuccess)
        {
            actions.Add(ServiceOrderActions.Complete);
        }

        return actions;
    }

    public bool May(string policy) => AppPolicies.Roles[policy].Intersect(user.Roles, StringComparer.Ordinal).Any();
}
