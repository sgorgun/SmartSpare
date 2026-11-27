namespace SmartSpare.SharedKernel;

/// <summary>
/// Where a user may write (ADR 0026 decision 5): unrestricted (an administrator) or at the listed hubs only. An empty
/// list means no hub at all; there is no "no hub means everywhere". Reads are never limited by hub.
/// </summary>
public sealed record HubRights(bool Unrestricted, IReadOnlySet<short> Hubs)
{
    public static HubRights Everywhere { get; } = new(true, new HashSet<short>());

    public static HubRights At(params short[] hubs) => new(false, hubs.ToHashSet());

    public bool Allows(short warehouseId) => Unrestricted || Hubs.Contains(warehouseId);
}
