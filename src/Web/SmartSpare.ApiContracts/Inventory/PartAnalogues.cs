using System.ComponentModel;
using FluentValidation;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>The link types of ADR 0027.</summary>
public static class AnalogueLinkTypes
{
    /// <summary>Either part may be fitted for the other; groups are connected components (transitive).</summary>
    public const string Interchangeable = "interchangeable";

    /// <summary>One part replaces another; at most one successor per part, no cycles.</summary>
    public const string Supersedes = "supersedes";

    public static IReadOnlyList<string> All { get; } = [Interchangeable, Supersedes];
}

/// <summary>How a link looks from one part: interchangeable, this part replaces the other, or is replaced by it.</summary>
public static class AnalogueRelations
{
    public const string Interchangeable = "interchangeable";

    public const string Replaces = "replaces";

    public const string ReplacedBy = "replacedBy";
}

/// <summary>A part as an analogue view names it.</summary>
[Description("A part as an analogue view names it.")]
public sealed record PartRef(
    [property: Description("Part id.")] long Id,
    [property: Description("Brand code.")] string BrandCode,
    [property: Description("OEM part number.")] string OemPartNumber,
    [property: Description("Description.")] string Description,
    [property: Description("Archived parts stay in history and cannot be linked anew.")] bool IsArchived);

/// <summary>A link seen from one part.</summary>
[Description("A link between two parts, seen from one of them.")]
public sealed record PartAnalogueLink(
    [property: Description("Link id.")] long Id,
    [property: Description("interchangeable, replaces (this part supersedes the other) or replacedBy (the other supersedes this part).")] string Relation,
    [property: Description("The other part.")] PartRef Other,
    [property: Description("Units of the analogue per unit of the part, applied to the analogue's demand history (US15).")] decimal ScalingFactor,
    [property: Description("Date the link applies from.")] DateOnly ValidFrom,
    [property: Description("Note, if any.")] string? Note,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A part's analogues: its links, its interchangeability group and its supersession chain (ADR 0027).</summary>
[Description("A part's links, its interchangeability group and its supersession chain.")]
public sealed record PartAnaloguesView(
    [property: Description("The part.")] PartRef Part,
    [property: Description("Every link of the part.")] IReadOnlyList<PartAnalogueLink> Links,
    [property: Description("The part's interchangeability group (connected component of interchangeable links), the part included.")] IReadOnlyList<PartRef> Group,
    [property: Description("The supersession chain from the part to the current part at its end; just the part when nothing replaces it.")] IReadOnlyList<PartRef> Chain);

/// <summary>The kinds of a direct analogue: a direct interchangeable link or the current successor (PR 7i3).</summary>
public static class DirectAnalogueKinds
{
    /// <summary>Linked directly as interchangeable (not merely in the same forecasting group).</summary>
    public const string Interchangeable = "interchangeable";

    /// <summary>The current part at the end of the part's supersession chain.</summary>
    public const string Successor = "successor";
}

/// <summary>A part that may stand in for another, with what is available at a hub (read-only, PR 7i3).</summary>
[Description("A direct analogue of a part with what is available at the hub.")]
public sealed record DirectAnalogue(
    [property: Description("The analogue part.")] PartRef Part,
    [property: Description("interchangeable (a direct link) or successor (the end of the supersession chain).")] string Kind,
    [property: Description("Units of the analogue per unit of the part (product along the chain for a successor).")] decimal ScalingFactor,
    [property: Description("On hand minus reserved at the hub; never raw on hand.")] decimal Available);

/// <summary>The direct analogues of one part at one hub.</summary>
[Description("The direct analogues of one part at one hub: direct interchangeable links and the current successor, never indirect group members.")]
public sealed record DirectAnalogues(
    [property: Description("The part asked for.")] long PartId,
    [property: Description("The hub of the availability.")] short WarehouseId,
    [property: Description("Its direct analogues, those available first.")] IReadOnlyList<DirectAnalogue> Analogues);

/// <summary>A stored link.</summary>
[Description("A typed link between two parts.")]
public sealed record PartAnalogueDto(
    [property: Description("Link id.")] long Id,
    [property: Description("For a supersession the new part; for an interchangeable link the smaller id.")] long PartId,
    [property: Description("For a supersession the old part it replaces.")] long AnaloguePartId,
    [property: Description("interchangeable or supersedes.")] string LinkType,
    [property: Description("Units of the analogue per unit of the part (US15).")] decimal ScalingFactor,
    [property: Description("Date the link applies from.")] DateOnly ValidFrom,
    [property: Description("Note, if any.")] string? Note,
    [property: Description("Concurrency version; send it back as If-Match.")] string Version);

/// <summary>A new link. For a supersession, <c>partId</c> replaces <c>analoguePartId</c>.</summary>
[Description("A new link between two parts; for a supersession, partId (new) replaces analoguePartId (old).")]
public sealed record PartAnalogueCreate(
    [property: Description("The part; for a supersession the new part.")] long PartId,
    [property: Description("The analogue; for a supersession the old part.")] long AnaloguePartId,
    [property: Description("interchangeable or supersedes.")] string LinkType,
    [property: Description("Units of the analogue per unit of the part, more than 0 and at most 10; 1 when the parts are one-to-one.")] decimal ScalingFactor,
    [property: Description("Date the link applies from; today when empty.")] DateOnly? ValidFrom,
    [property: Description("Optional note, at most 300 characters.")] string? Note);

/// <summary>A changed link. A change of type or direction needs the user's reason (ADR 0027 decision 6).</summary>
[Description("A changed link; changing its type or direction needs a reason, which goes into the audit trail.")]
public sealed record PartAnalogueUpdate(
    [property: Description("interchangeable or supersedes.")] string LinkType,
    [property: Description("For a supersession, which of the two parts is the new one; ignored for an interchangeable link.")] long? ReplacingPartId,
    [property: Description("Units of the analogue per unit of the part, more than 0 and at most 10.")] decimal ScalingFactor,
    [property: Description("Date the link applies from.")] DateOnly ValidFrom,
    [property: Description("Optional note, at most 300 characters.")] string? Note,
    [property: Description("Why the type or direction changes; required then (400 Inventory.ReasonRequired), optional otherwise.")] string? Reason);

/// <summary>The removal of a link, with the user's reason (ADR 0027 decision 6).</summary>
[Description("The removal of a link with the user's reason, which goes into the audit trail.")]
public sealed record RemoveAnalogueRequest(
    [property: Description("Why the link is removed; required (400 Inventory.ReasonRequired).")] string Reason);

/// <summary>Shape rules of a new link, the same on the form and the server.</summary>
public sealed class PartAnalogueCreateValidator : AbstractValidator<PartAnalogueCreate>
{
    public PartAnalogueCreateValidator()
    {
        RuleFor(l => l.PartId).GreaterThan(0);
        RuleFor(l => l.AnaloguePartId).GreaterThan(0);
        RuleFor(l => l.LinkType).Must(t => AnalogueLinkTypes.All.Contains(t)).WithMessage("The link type is interchangeable or supersedes.");
        RuleFor(l => l.ScalingFactor).GreaterThan(0).LessThanOrEqualTo(10);
        RuleFor(l => l.Note).MaximumLength(300);
    }
}

/// <summary>Shape rules of a changed link; the reason rule is the server's (a distinct error code).</summary>
public sealed class PartAnalogueUpdateValidator : AbstractValidator<PartAnalogueUpdate>
{
    public PartAnalogueUpdateValidator()
    {
        RuleFor(l => l.LinkType).Must(t => AnalogueLinkTypes.All.Contains(t)).WithMessage("The link type is interchangeable or supersedes.");
        RuleFor(l => l.ScalingFactor).GreaterThan(0).LessThanOrEqualTo(10);
        RuleFor(l => l.Note).MaximumLength(300);
        RuleFor(l => l.Reason).MaximumLength(300);
    }
}
