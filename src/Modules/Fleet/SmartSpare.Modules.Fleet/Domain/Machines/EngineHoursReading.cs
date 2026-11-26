using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Domain.Machines;

/// <summary>PostgreSQL enum <c>fleet.reading_source</c>.</summary>
internal enum ReadingSource
{
    Manual,
    Telematics,
    ServiceVisit,
    Import,
}

/// <summary>
/// One odometer reading of a machine (US05). Append-only evidence for warranty and OEM compliance: there is no
/// update method, and <c>trg_engine_hours_immutable</c> forbids edits in the database. Inserted by
/// <c>fleet.log_engine_hours</c>, never by EF.
/// </summary>
internal sealed class EngineHoursReading : Entity<long>
{
    private EngineHoursReading()
        : base(0)
    {
    }

    internal EngineHoursReading(long machineId, decimal readingHours, DateTimeOffset readAt, ReadingSource source, Guid? recordedBy = null)
        : base(0)
    {
        MachineId = machineId;
        ReadingHours = readingHours;
        ReadAt = readAt;
        Source = source;
        RecordedBy = recordedBy;
    }

    public long MachineId { get; private set; }

    public decimal ReadingHours { get; private set; }

    /// <summary>Time the reading was taken in the field (may be earlier than <see cref="CreatedAt"/> for offline entry).</summary>
    public DateTimeOffset ReadAt { get; private set; }

    public ReadingSource Source { get; private set; }

    public Guid? RecordedBy { get; private set; }

    /// <summary>Set by the database default.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
