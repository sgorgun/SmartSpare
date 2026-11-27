using System.ComponentModel;

namespace SmartSpare.ApiContracts.MasterData;

/// <summary>The body of a catalogue create or update (ADR 0025): every catalogue's write has a code and a name.</summary>
public interface ICatalogWrite
{
    /// <summary>Business key. Fixed after creation; only an administrator may change it.</summary>
    string Code { get; }

    string Name { get; }
}

/// <summary>Limits shared by every catalogue endpoint.</summary>
public static class CatalogLimits
{
    public const int MaxSearchLength = 100;
    public const int DefaultLookupLimit = 20;
    public const int MaxLookupLimit = 50;
    public const int DefaultHistoryPageSize = 20;
    public const int MaxPageSize = 100;
}

/// <summary>An active catalogue entry for an autocomplete field.</summary>
[Description("An active catalogue entry for an autocomplete field.")]
public sealed record CatalogLookupItem(
    [property: Description("Record id.")] long Id,
    [property: Description("Business key.")] string Code,
    [property: Description("Display name.")] string Name,
    [property: Description("A short description to tell similar entries apart, if the catalogue has one.")] string? Detail);

/// <summary>One change of a catalogue record: who did what and when.</summary>
[Description("One change of a catalogue record: who did what and when.")]
public sealed record RecordHistoryEntry(
    [property: Description("When the change was made.")] DateTimeOffset At,
    [property: Description("created, changed, archived, restored or deleted.")] string Action,
    [property: Description("Name of the employee who made it (first name and initial); null for the system.")] string? Actor,
    [property: Description("The fields that changed, with their old and new values.")] IReadOnlyList<RecordChange> Changes,
    [property: Description("The justification given, if any.")] string? Reason);

/// <summary>A field of a record before and after a change.</summary>
[Description("A field of a record before and after a change.")]
public sealed record RecordChange(
    [property: Description("Field name in plain words.")] string Field,
    [property: Description("Value before the change; null when created.")] string? From,
    [property: Description("Value after the change.")] string? To);
