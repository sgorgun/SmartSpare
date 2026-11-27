namespace SmartSpare.Infrastructure.Persistence.Platform;

// Persistence model of the Platform schemas ref, util and audit (MODULE_MAPPING section 1.1).
// Plain mapping classes, not domain objects: reference data is seeded by migrations and only read
// by the modules, which map the ref tables they reference with their own read-only types.

internal sealed class UnitOfMeasure
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool IsFractional { get; set; }
}

/// <summary>
/// An assignment role of a responsible employee per record kind (ADR 0026, STAGE_7_PLAN 1.1), e.g. BRAND_MANAGER of a brand
/// or KEEPER of a warehouse. <see cref="GrantsScope"/> marks the roles that give rights (brand scope, hub access). Seeded
/// reference data: what a role means is defined by code, not edited.
/// </summary>
internal sealed class AssignmentRole
{
    public string RecordKind { get; set; } = null!;

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public bool GrantsScope { get; set; }
}

internal sealed class Brand
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string CountryCode { get; set; } = null!;
}

internal sealed class MachineType
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;
}

internal sealed class PartCategory
{
    public int Id { get; set; }

    public int? ParentId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public PartCategory? Parent { get; set; }
}

internal sealed class OperatingCondition
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public decimal IntervalFactor { get; set; }
}

internal sealed class MovementType
{
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public short BalanceSign { get; set; }

    public bool IsDemand { get; set; }

    public string? Description { get; set; }
}

internal sealed class DemandClass
{
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public decimal AdiLower { get; set; }

    public decimal AdiUpper { get; set; }

    public decimal Cv2Lower { get; set; }

    public decimal Cv2Upper { get; set; }

    public string? Description { get; set; }
}

internal sealed class SeasonPhase
{
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;
}

internal sealed class AgroCalendarWeek
{
    public short IsoWeek { get; set; }

    public string SeasonPhaseCode { get; set; } = null!;

    public decimal SeasonalWeight { get; set; }

    public SeasonPhase SeasonPhase { get; set; } = null!;
}

internal sealed class DocumentCounter
{
    public string Prefix { get; set; } = null!;

    public short Year { get; set; }

    public int LastValue { get; set; }
}

/// <summary>Row of the partitioned, append-only <c>audit.audit_log</c> (created in SQL, read-only here).</summary>
internal sealed class AuditLogEntry
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public Guid? UserId { get; set; }

    public string DbRole { get; set; } = null!;

    public string Action { get; set; } = null!;

    public string TableName { get; set; } = null!;

    public string RecordId { get; set; } = null!;

    public string? OldValues { get; set; }

    public string? NewValues { get; set; }

    public string[]? ChangedColumns { get; set; }

    public string? Reason { get; set; }

    public long TransactionId { get; set; }
}
