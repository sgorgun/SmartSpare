using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Modules.Fleet.Domain.Machines;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

/// <summary>A service order drafted by <c>fleet.log_engine_hours</c>.</summary>
internal sealed record DraftedOrder(long ServiceOrderId, string ServiceOrderNumber, decimal DueEngineHours, bool FullyReserved);

/// <summary>
/// The SQL behind recording a reading (STAGE_6_PLAN 3.1): the machine row lock, the reading lookups and
/// <c>fleet.log_engine_hours</c>, which inserts the reading (the triggers repeat every odometer rule) and drafts the
/// services that became due. Runs inside the command's transaction.
/// </summary>
internal sealed class ReadingStore(FleetDbContext db)
{
    /// <summary>
    /// Locks the machine row until the transaction ends, so concurrent readings of one machine are handled one after the
    /// other: the second sees the first one's reading in its checks. False if the machine does not exist.
    /// </summary>
    public async Task<bool> LockMachineAsync(long machineId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM fleet.machine WHERE id = @id FOR UPDATE", new NpgsqlParameter<long>("id", machineId))
            .ToListAsync(cancellationToken).ConfigureAwait(false) is [_];

    public Task<Machine> MachineAsync(long machineId, CancellationToken cancellationToken) =>
        db.Set<Machine>().AsNoTracking().SingleAsync(m => m.Id == machineId, cancellationToken);

    public Task<EngineHoursReading?> LastReadingAsync(long machineId, CancellationToken cancellationToken) =>
        db.Set<EngineHoursReading>().AsNoTracking().Where(r => r.MachineId == machineId).OrderByDescending(r => r.ReadAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<EngineHoursReading?> ReadingAtAsync(long machineId, DateTimeOffset readAt, CancellationToken cancellationToken) =>
        db.Set<EngineHoursReading>().AsNoTracking().SingleOrDefaultAsync(r => r.MachineId == machineId && r.ReadAt == readAt, cancellationToken);

    /// <summary><c>fleet.log_engine_hours</c>: stores the reading and drafts every plan due within 50 h.</summary>
    public async Task<IReadOnlyList<DraftedOrder>> LogAsync(long machineId, decimal hours, DateTimeOffset readAt, ReadingSource source, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<DraftedOrder>(
                "SELECT * FROM fleet.log_engine_hours(@machine, @hours, @read_at, @source::fleet.reading_source)",
                new NpgsqlParameter<long>("machine", machineId),
                new NpgsqlParameter<decimal>("hours", hours),
                new NpgsqlParameter<DateTimeOffset>("read_at", readAt),
                new NpgsqlParameter<string>("source", SqlName(source)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The drafted orders with their kit lines, for <c>ServiceOrderDrafted</c>.</summary>
    public async Task<IReadOnlyList<ServiceOrder>> OrdersAsync(IReadOnlyCollection<long> orderIds, CancellationToken cancellationToken) =>
        await db.Set<ServiceOrder>().AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).OrderBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static string SqlName(ReadingSource source) => source switch
    {
        ReadingSource.Telematics => "telematics",
        ReadingSource.ServiceVisit => "service_visit",
        ReadingSource.Import => "import",
        _ => "manual",
    };
}
