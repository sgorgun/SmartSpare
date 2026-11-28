using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Catalog;

/// <summary>PostgreSQL enum <c>catalog.analogue_link_type</c> (ADR 0027).</summary>
internal enum AnalogueLinkType
{
    /// <summary>Either part may be fitted for the other; symmetric, transitive within a group.</summary>
    Interchangeable,

    /// <summary><c>PartId</c> (new) replaces <c>AnaloguePartId</c> (old).</summary>
    Supersedes,
}

/// <summary>
/// A typed link between two parts (<c>catalog.part_analogue</c>, ADR 0027). An interchangeable link is stored once with
/// the smaller part id first; a supersession points from the new part to the old one. The scaling factor is the number
/// of units of the analogue per unit of the part (US15). Rules that need the other links (one link per pair, one
/// successor, no supersession cycle) are <see cref="AnalogueRules"/>, and the database enforces them too (SS502).
/// </summary>
internal sealed class PartAnalogue : AggregateRoot<long>
{
    public const int MaxNoteLength = 300;

    public const decimal MaxScalingFactor = 10m;

    private PartAnalogue()
        : base(default)
    {
    }

    private PartAnalogue(long partId, long analoguePartId, AnalogueLinkType linkType)
        : base(default)
    {
        PartId = partId;
        AnaloguePartId = analoguePartId;
        LinkType = linkType;
    }

    public long PartId { get; private set; }

    public long AnaloguePartId { get; private set; }

    public AnalogueLinkType LinkType { get; private set; }

    public decimal ScalingFactor { get; private set; } = 1m;

    public DateOnly ValidFrom { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new link; an interchangeable one is stored with the smaller id first.</summary>
    public static Result<PartAnalogue> Create(
        long partId, long analoguePartId, AnalogueLinkType linkType, decimal scalingFactor, DateOnly validFrom, string? note, Guid? createdBy)
    {
        if (partId == analoguePartId)
        {
            return AnalogueRules.SelfLink;
        }

        var (first, second) = Ordered(partId, analoguePartId, linkType);
        var link = new PartAnalogue(first, second, linkType) { CreatedBy = createdBy, ValidFrom = validFrom };
        var details = link.ChangeDetails(scalingFactor, validFrom, note);
        return details.IsFailure ? details.Error : link;
    }

    /// <summary>Changes the factor, the date and the note; <see langword="true"/> if anything changed.</summary>
    public Result<bool> ChangeDetails(decimal scalingFactor, DateOnly validFrom, string? note)
    {
        if (scalingFactor is <= 0 or > MaxScalingFactor)
        {
            return AnalogueRules.InvalidFactor;
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            return AnalogueRules.InvalidNote;
        }

        var changed = ScalingFactor != scalingFactor || ValidFrom != validFrom || Note != trimmed;
        ScalingFactor = scalingFactor;
        ValidFrom = validFrom;
        Note = trimmed;
        return changed;
    }

    /// <summary>
    /// Changes the type, keeping the direction given: <paramref name="newPartId"/> is the new part of a supersession.
    /// The caller has the user's reason (ADR 0027 decision 6); <see langword="true"/> if the type or direction changed.
    /// </summary>
    public bool Retype(AnalogueLinkType linkType, long newPartId)
    {
        var other = newPartId == PartId ? AnaloguePartId : PartId;
        var (first, second) = Ordered(newPartId, other, linkType);
        var changed = LinkType != linkType || PartId != first || AnaloguePartId != second;
        LinkType = linkType;
        PartId = first;
        AnaloguePartId = second;
        return changed;
    }

    private static (long First, long Second) Ordered(long partId, long analoguePartId, AnalogueLinkType linkType) =>
        linkType == AnalogueLinkType.Interchangeable && partId > analoguePartId ? (analoguePartId, partId) : (partId, analoguePartId);
}

/// <summary>An existing link as the rules see it.</summary>
internal sealed record AnalogueLinkFact(long Id, long PartId, long AnaloguePartId, AnalogueLinkType LinkType);

/// <summary>
/// The rules of ADR 0027 decision 4 over the existing links, for readable messages and per-row CSV errors before the
/// database refuses the same with SS502: one link per unordered pair, at most one successor per part, no supersession
/// cycle. Interchangeable links form groups and are not part of the cycle check.
/// </summary>
internal static class AnalogueRules
{
    public static readonly Error SelfLink = Error.BusinessRule("SS502", "A part cannot be its own analogue.");

    public static readonly Error InvalidFactor = Error.Validation(
        "Inventory.InvalidScalingFactor", $"The scaling factor is more than 0 and at most {PartAnalogue.MaxScalingFactor}.");

    public static readonly Error InvalidNote = Error.Validation("Inventory.InvalidNote", $"The note has at most {PartAnalogue.MaxNoteLength} characters.");

    public static readonly Error Duplicate = Error.BusinessRule("SS502", "These two parts are already linked; change or remove that link instead.");

    public static readonly Error SecondSuccessor = Error.BusinessRule("SS502", "The old part already has a successor: a part is replaced by at most one other.");

    public static readonly Error Cycle = Error.BusinessRule("SS502", "This supersession would close a cycle: a part would replace itself through the chain.");

    /// <summary>Checks a new or changed link (<paramref name="candidate"/> with id 0 when new) against the others.</summary>
    public static Result Check(AnalogueLinkFact candidate, IReadOnlyCollection<AnalogueLinkFact> existing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        if (candidate.PartId == candidate.AnaloguePartId)
        {
            return SelfLink;
        }

        var others = existing.Where(l => l.Id != candidate.Id || candidate.Id == 0).ToList();
        if (others.Exists(l => SamePair(l, candidate)))
        {
            return Duplicate;
        }

        if (candidate.LinkType != AnalogueLinkType.Supersedes)
        {
            return Result.Success();
        }

        if (others.Exists(l => l.LinkType == AnalogueLinkType.Supersedes && l.AnaloguePartId == candidate.AnaloguePartId))
        {
            return SecondSuccessor;
        }

        // The candidate says "new replaces old". It closes a cycle if the old part already replaces the new one through the
        // chain: walk from the old part along "replaces" (part → analogue) and look for the new part.
        var olderOf = others.Where(l => l.LinkType == AnalogueLinkType.Supersedes).ToLookup(l => l.PartId, l => l.AnaloguePartId);
        var seen = new HashSet<long>();
        var queue = new Queue<long>([candidate.AnaloguePartId]);
        while (queue.Count > 0)
        {
            var part = queue.Dequeue();
            if (part == candidate.PartId)
            {
                return Cycle;
            }

            if (seen.Add(part))
            {
                foreach (var older in olderOf[part])
                {
                    queue.Enqueue(older);
                }
            }
        }

        return Result.Success();
    }

    private static bool SamePair(AnalogueLinkFact a, AnalogueLinkFact b) =>
        Math.Min(a.PartId, a.AnaloguePartId) == Math.Min(b.PartId, b.AnaloguePartId)
        && Math.Max(a.PartId, a.AnaloguePartId) == Math.Max(b.PartId, b.AnaloguePartId);
}
