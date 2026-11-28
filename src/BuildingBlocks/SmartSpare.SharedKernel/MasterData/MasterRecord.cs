using System.Text.RegularExpressions;

namespace SmartSpare.SharedKernel.MasterData;

/// <summary>
/// A row of a catalogue (master data, ADR 0025): a business <see cref="Code"/> that never changes after creation, a
/// display <see cref="Name"/>, and archiving instead of deletion. Archived records stay referenced by history but
/// cannot be chosen by new documents (SS500); archiving runs the catalogue's guard first (SS501, checked by the
/// handler). The concurrency version (<c>xmin</c>) and the audit trigger belong to the table, not the class.
/// </summary>
public abstract class MasterRecord<TId> : AggregateRoot<TId>
    where TId : notnull
{
    protected MasterRecord()
        : base(default!)
    {
        Code = null!;
        Name = null!;
    }

    protected MasterRecord(string code, string name)
        : base(default!)
    {
        Code = code;
        Name = name;
    }

    /// <summary>Business key, unique in the catalogue; used by CSV files and other systems, so it never changes.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>User who archived the record (<c>iam.app_user</c>).</summary>
    public Guid? ArchivedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Archives the record; archiving an archived record changes nothing.</summary>
    public void Archive(DateTimeOffset now, Guid? by)
    {
        if (IsArchived)
        {
            return;
        }

        IsArchived = true;
        ArchivedAt = now;
        ArchivedBy = by;
    }

    /// <summary>Makes an archived record choosable again (the handler re-checks the code's uniqueness).</summary>
    public void Restore()
    {
        IsArchived = false;
        ArchivedAt = null;
        ArchivedBy = null;
    }

    /// <summary>
    /// Replaces the business key with an already normalised and unique code. Only an administrator may do this (the
    /// handler checks), because files and other systems refer to records by code.
    /// </summary>
    public void ChangeCode(string normalisedCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalisedCode);
        Code = normalisedCode;
    }

    /// <summary>Sets the name; <see langword="true"/> if it changed.</summary>
    protected Result<bool> Rename(string? name) => Rename(name, MasterRecordRules.MaxNameLength);

    /// <summary>Sets the name of a catalogue whose name column is longer (parts: a 300-character description).</summary>
    protected Result<bool> Rename(string? name, int maxLength)
    {
        var normalised = MasterRecordRules.NormaliseName(name, maxLength);
        if (normalised.IsFailure)
        {
            return normalised.Error;
        }

        var changed = !string.Equals(Name, normalised.Value, StringComparison.Ordinal);
        Name = normalised.Value;
        return changed;
    }
}

/// <summary>The rules every catalogue shares for codes and names.</summary>
public static class MasterRecordRules
{
    /// <summary>Length of <c>name</c> in every catalogue.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Upper-cases and trims a code and checks it against the catalogue's pattern.</summary>
    public static Result<string> NormaliseCode(string? code, Regex pattern, Error invalid)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        return pattern.IsMatch(normalised) ? normalised : invalid;
    }

    /// <summary>A trimmed name of 1 to <see cref="MaxNameLength"/> characters.</summary>
    public static Result<string> NormaliseName(string? name) => NormaliseName(name, MaxNameLength);

    /// <summary>A trimmed name of 1 to <paramref name="maxLength"/> characters.</summary>
    public static Result<string> NormaliseName(string? name, int maxLength)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length > 0 && trimmed.Length <= maxLength ? trimmed : CatalogErrors.InvalidName;
    }
}
