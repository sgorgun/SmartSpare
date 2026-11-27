namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// Separation of duties (ADR 0028): pairs of permissions that no role, and no position (the union of its roles), may
/// hold together. A save that would break a pair is refused with 409 and the pair. Short and declarative on purpose;
/// the superadministrator's locked role and position are exempt. Self-granting is a separate rule (nobody changes a
/// role they hold or their own position), and self-approval stays forbidden by the four-eyes check.
/// </summary>
public static class DutyConflicts
{
    public static IReadOnlyList<(string First, string Second)> Pairs { get; } =
    [
        // Whoever creates purchase orders must not also maintain the suppliers they buy from.
        (Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.Supplier.Edit),
    ];

    /// <summary>The first pair that <paramref name="permissions"/> holds completely, or <see langword="null"/>.</summary>
    public static (string First, string Second)? FindIn(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in Pairs)
        {
            if (set.Contains(pair.First) && set.Contains(pair.Second))
            {
                return pair;
            }
        }

        return null;
    }
}
