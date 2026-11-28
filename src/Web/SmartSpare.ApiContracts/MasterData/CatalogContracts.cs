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

    /// <summary>Data rows of a catalogue CSV import (STAGE_7_PLAN 2.6, the same as the parts import).</summary>
    public const int MaxCsvRows = 50_000;

    /// <summary>Bytes of a catalogue CSV import: 10 MB.</summary>
    public const long MaxCsvBytes = 10 * 1024 * 1024;

    /// <summary>Catalogue CSV imports a user may start in a minute.</summary>
    public const int CsvImportsPerMinute = 5;

    /// <summary>Above this share of rows with errors nothing is written (the 5 % gate of <c>staging.process_batch</c>).</summary>
    public const decimal CsvErrorGate = 0.05m;
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

/// <summary>The outcome of a catalogue CSV import or of its dry run (STAGE_7_PLAN 2.6).</summary>
[Description("The outcome of a catalogue CSV import or of its dry run.")]
public sealed record CatalogImportResult(
    [property: Description("True: the import ran in full and was rolled back, nothing was kept.")] bool DryRun,
    [property: Description("True: the changes were written (never for a dry run, nor above the 5 % error gate).")] bool Applied,
    [property: Description("Data rows in the file.")] int Rows,
    [property: Description("Rows that create a new record.")] int Created,
    [property: Description("Rows that change an existing record.")] int Updated,
    [property: Description("Rows equal to the existing record; they are not written.")] int Unchanged,
    [property: Description("Row errors in file order; above 5 % of the rows nothing is written.")] IReadOnlyList<CatalogImportError> Errors);

/// <summary>A row the import refused.</summary>
[Description("A row the import refused.")]
public sealed record CatalogImportError(
    [property: Description("Line in the file (the header is line 1).")] int Line,
    [property: Description("Column the error is about, if one.")] string? Column,
    [property: Description("Error code, for example DUPLICATE, ARCHIVED, INVALID.")] string Code,
    [property: Description("The error in plain words.")] string Message);
