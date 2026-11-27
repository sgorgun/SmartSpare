namespace SmartSpare.SharedKernel.MasterData;

/// <summary>The errors every catalogue shares (ADR 0025, STAGE_7_PLAN 2.4).</summary>
public static class CatalogErrors
{
    public static readonly Error VersionRequired = Error.Validation(
        "Catalog.VersionRequired", "Send the version you read as If-Match, so a concurrent change is not overwritten.");

    public static readonly Error NotFound = Error.NotFound("Catalog.NotFound", "The record does not exist.");

    public static readonly Error CodeTaken = Error.Conflict("Catalog.CodeTaken", "Another record already has this code.");

    public static readonly Error CodeFixed = Error.BusinessRule(
        "Catalog.CodeFixed", "The code is fixed after creation, because files and other systems refer to it. Only an administrator can change it.");

    public static readonly Error InvalidPaging = Error.Validation("Catalog.InvalidPaging", "The page starts at 1 and holds 1 to 100 records.");

    public static readonly Error BodyRequired = Error.Validation("Catalog.BodyRequired", "Send the record as a JSON body.");

    public static readonly Error InvalidName = Error.Validation("Catalog.InvalidName", $"The name needs 1 to {MasterRecordRules.MaxNameLength} characters.");

    /// <summary>SS500: a write refers to an archived record, which new documents and records cannot choose.</summary>
    public static Error Archived(string what) => Error.BusinessRule("SS500", $"The {what} is archived and cannot be chosen.");

    /// <summary>SS501: the catalogue's guard refused to archive; the reasons say what still uses the record.</summary>
    public static ArchiveBlockedError ArchiveBlocked(IReadOnlyList<string> reasons) => new(reasons);
}

/// <summary>SS501 with the list of what blocks archiving (e.g. "on hand 12 at KLP"), returned as <c>reasons</c>.</summary>
public sealed record ArchiveBlockedError(IReadOnlyList<string> Reasons)
    : Error("SS501", "The record cannot be archived while it is in use.", ErrorType.Conflict);
