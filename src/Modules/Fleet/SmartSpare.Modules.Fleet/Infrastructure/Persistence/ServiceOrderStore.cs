using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.Modules.Fleet.Domain.Machines;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

/// <summary>
/// The data access of the service order commands (STAGE_6_PLAN 3.2): the aggregate with its lines, the plan and part
/// checks, and <c>fleet.create_service_order_draft</c> for a planned service. Runs inside the command's transaction.
/// </summary>
internal sealed class ServiceOrderStore(FleetDbContext db)
{
    public Task<ServiceOrder?> TrackedAsync(long id, CancellationToken cancellationToken) =>
        db.Set<ServiceOrder>().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<ServiceOrder> ReadAsync(long id, CancellationToken cancellationToken) =>
        db.Set<ServiceOrder>().AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == id, cancellationToken);

    public Task<Machine?> MachineAsync(long id, CancellationToken cancellationToken) =>
        db.Set<Machine>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<MaintenancePlan?> PlanAsync(int id, CancellationToken cancellationToken) =>
        db.Set<MaintenancePlan>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.IsActive, cancellationToken);

    /// <summary>The ids among <paramref name="partIds"/> that are active parts of Fleet's catalogue copy.</summary>
    public async Task<IReadOnlySet<long>> ActivePartsAsync(IReadOnlyCollection<long> partIds, CancellationToken cancellationToken) =>
        (await db.Set<PartSnapshot>().AsNoTracking().Where(p => partIds.Contains(p.PartId) && p.IsActive).Select(p => p.PartId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

    /// <summary><c>fleet.create_service_order_draft</c>: the order and its kit from the plan, at threshold <paramref name="due"/>.</summary>
    public async Task<long> DraftFromPlanAsync(long machineId, int planId, decimal due, CancellationToken cancellationToken) =>
        (await db.Database.SqlQueryRaw<long>(
                "SELECT fleet.create_service_order_draft(@machine, @plan, @due) AS \"Value\"",
                new NpgsqlParameter<long>("machine", machineId), new NpgsqlParameter<int>("plan", planId), new NpgsqlParameter<decimal>("due", due))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

    public void Add(ServiceOrder order) => db.Add(order);
}
