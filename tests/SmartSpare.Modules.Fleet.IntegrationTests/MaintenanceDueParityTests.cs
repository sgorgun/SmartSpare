using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Infrastructure.Queries;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The C# due calculation and the SQL function that drafts service orders agree (STAGE_6_PLAN section 2.1,
/// MAINTENANCE_PLANNING.md section 4): for every active machine and plan of the demo fleet, the inputs read by
/// <see cref="MaintenanceDueInputs"/> give the same next threshold D and remaining hours R in
/// <see cref="MaintenanceDueCalculator"/> as <c>fleet.fn_maintenance_due</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(FleetDemoDatabaseGroup.Name)]
public sealed class MaintenanceDueParityTests(FleetDemoDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_calculator_and_fn_maintenance_due_agree_on_every_machine_and_plan_of_the_demo_fleet()
    {
        await using var db = database.CreateContext();
        var inputs = await MaintenanceDueInputs.LoadAsync(db, null, null, Ct);

        // An unlimited horizon makes the function return every plan, not only those due within 50 h.
        var sql = await db.Database.SqlQueryRaw<SqlDue>(
                """
                SELECT m.id AS machine_id, d.maintenance_plan_id, d.effective_interval, d.due_engine_hours, d.remaining_hours
                FROM fleet.machine m
                CROSS JOIN LATERAL fleet.fn_maintenance_due(m.id, 1000000) d
                WHERE m.status = 'active'
                """)
            .ToListAsync(Ct);

        inputs.Count.ShouldBeGreaterThan(100);   // tractors and combines of the 500 machines, two plans each
        var expected = sql.ToDictionary(r => (r.MachineId, r.MaintenancePlanId));
        expected.Count.ShouldBe(inputs.Count);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var mismatches = inputs
            .Select(i => (i.MachineId, i.MaintenancePlanId, Due: MaintenanceDueCalculator.Calculate(i.Input, today), Sql: expected[(i.MachineId, i.MaintenancePlanId)]))
            .Where(x => x.Due.EffectiveInterval != x.Sql.EffectiveInterval
                     || x.Due.DueEngineHours != x.Sql.DueEngineHours
                     || x.Due.RemainingHours != x.Sql.RemainingHours)
            .Select(x => $"machine {x.MachineId} plan {x.MaintenancePlanId}: C# E={x.Due.EffectiveInterval} D={x.Due.DueEngineHours} R={x.Due.RemainingHours}, SQL E={x.Sql.EffectiveInterval} D={x.Sql.DueEngineHours} R={x.Sql.RemainingHours}")
            .ToList();

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_UC02_Lexion_is_planned_after_its_500_hour_service_was_drafted()
    {
        await using var db = database.CreateContext();
        var machineId = await db.Database.SqlQueryRaw<long>("SELECT id AS \"Value\" FROM fleet.machine WHERE vin = 'LX901PNV2026'").SingleAsync(Ct);

        var inputs = await MaintenanceDueInputs.LoadAsync(db, [machineId], null, Ct);
        var engine = inputs.Single(i => i.Input.IntervalHours == 500);
        var due = MaintenanceDueCalculator.Calculate(engine.Input, DateOnly.FromDateTime(DateTime.UtcNow));

        // 440 h at a visit, then 512 h logged through fleet.log_engine_hours, which drafted the 500 h service: the open
        // order counts as served, so the next threshold is 1,000 h.
        engine.Input.Readings.Select(r => r.Hours).ShouldBe([440m, 512m]);
        engine.Input.IntervalMonths.ShouldBe((short)12);
        engine.Input.OpenOrderThreshold.ShouldBe(500m);
        due.DueEngineHours.ShouldBe(1000m);
        due.Status.ShouldBe(MaintenanceDueStatus.Planned);
    }

    [Fact]
    public async Task The_inputs_can_be_limited_to_one_home_hub()
    {
        await using var db = database.CreateContext();
        var pnv = await db.Database.SqlQueryRaw<short>("SELECT id AS \"Value\" FROM inventory.warehouse WHERE code = 'PNV'").SingleAsync(Ct);
        var expected = await db.Database.SqlQueryRaw<int>(
                """
                SELECT count(*)::int AS "Value" FROM fleet.machine m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id AND mp.is_active
                WHERE m.status = 'active' AND m.home_warehouse_id = {0}
                """, pnv)
            .SingleAsync(Ct);

        var inputs = await MaintenanceDueInputs.LoadAsync(db, null, pnv, Ct);

        inputs.Count.ShouldBe(expected);
        inputs.Count.ShouldBeGreaterThan(0);
    }

    internal sealed record SqlDue(long MachineId, int MaintenancePlanId, decimal EffectiveInterval, decimal DueEngineHours, decimal RemainingHours);
}
