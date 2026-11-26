using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;

namespace SmartSpare.Modules.Fleet.Infrastructure.Queries;

// Data access of the Fleet read side (STAGE_6_PLAN sections 2.6 and 3.3): read models that only read PostgreSQL, with
// raw SQL or EF Core projections. They live in Infrastructure; the calculation itself is the pure domain service
// MaintenanceDueCalculator. The unit-test metric of business logic covers Domain and Features, so this code is measured
// by the integration tests.

/// <summary>One active plan on one active machine with everything the due calculation needs.</summary>
internal sealed record MachinePlanDueInput(long MachineId, int MaintenancePlanId, MaintenanceDueInput Input);

/// <summary>
/// Reads the inputs of <see cref="MaintenanceDueCalculator"/> set-based (STAGE_6_PLAN section 2.6): one query for the
/// plans of the selected machines with their served, open and completed orders, one for their readings. Dates are UTC
/// days, as the calculator uses them. No calculation happens in SQL.
/// </summary>
internal static class MaintenanceDueInputs
{
    private const string PlansSql =
        """
        SELECT m.id AS machine_id, mp.id AS maintenance_plan_id, mp.interval_hours, mp.interval_months,
               oc.interval_factor, m.current_engine_hours, m.commissioned_on,
               served.last_served, served.open_threshold, served.last_completed_on
        FROM fleet.machine m
        JOIN ref.operating_condition oc ON oc.id = m.operating_condition_id
        JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id AND mp.is_active
        LEFT JOIN LATERAL (
            SELECT max(so.due_engine_hours) FILTER (WHERE so.status <> 'cancelled') AS last_served,
                   max(so.due_engine_hours) FILTER (WHERE so.status IN ('draft', 'parts_reserved', 'in_progress')) AS open_threshold,
                   max((so.completed_at AT TIME ZONE 'UTC')::date) FILTER (WHERE so.status = 'completed') AS last_completed_on
            FROM fleet.service_order so
            WHERE so.machine_id = m.id AND so.maintenance_plan_id = mp.id
        ) served ON true
        WHERE m.status = 'active'
          AND (@machines IS NULL OR m.id = ANY (@machines))
          AND (@warehouse IS NULL OR m.home_warehouse_id = @warehouse)
        ORDER BY m.id, mp.interval_hours
        """;

    private const string ReadingsSql =
        """
        SELECT r.machine_id, r.read_at, r.reading_hours
        FROM fleet.engine_hours_reading r
        JOIN fleet.machine m ON m.id = r.machine_id
        WHERE m.status = 'active'
          AND (@machines IS NULL OR m.id = ANY (@machines))
          AND (@warehouse IS NULL OR m.home_warehouse_id = @warehouse)
        ORDER BY r.machine_id, r.read_at
        """;

    /// <summary>Every active plan of the active machines, optionally limited to some machines or one home hub.</summary>
    public static async Task<IReadOnlyList<MachinePlanDueInput>> LoadAsync(
        FleetDbContext db, IReadOnlyCollection<long>? machineIds, short? warehouseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var plans = await db.Database.SqlQueryRaw<PlanRow>(PlansSql, Parameters(machineIds, warehouseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var readings = (await db.Database.SqlQueryRaw<ReadingRow>(ReadingsSql, Parameters(machineIds, warehouseId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.MachineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReadingPoint>)[.. g.Select(r => new ReadingPoint(r.ReadAt, r.ReadingHours))]);

        return
        [
            .. plans.Select(p =>
            {
                var history = readings.GetValueOrDefault(p.MachineId, []);
                return new MachinePlanDueInput(p.MachineId, p.MaintenancePlanId, new MaintenanceDueInput(
                    p.IntervalHours, p.IntervalMonths, p.IntervalFactor, p.CurrentEngineHours,
                    history.Count > 0 ? history[0].Hours : null,
                    p.LastServed, p.OpenThreshold, p.LastCompletedOn, p.CommissionedOn, history));
            }),
        ];
    }

    private static NpgsqlParameter[] Parameters(IReadOnlyCollection<long>? machineIds, short? warehouseId) =>
    [
        new("machines", DBNull.Value) { DataTypeName = "bigint[]", Value = machineIds is null ? DBNull.Value : machineIds.ToArray() },
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)warehouseId ?? DBNull.Value },
    ];

    internal sealed record PlanRow(
        long MachineId,
        int MaintenancePlanId,
        int IntervalHours,
        short? IntervalMonths,
        decimal IntervalFactor,
        decimal CurrentEngineHours,
        DateOnly? CommissionedOn,
        decimal? LastServed,
        decimal? OpenThreshold,
        DateOnly? LastCompletedOn);

    internal sealed record ReadingRow(long MachineId, DateTimeOffset ReadAt, decimal ReadingHours);
}
