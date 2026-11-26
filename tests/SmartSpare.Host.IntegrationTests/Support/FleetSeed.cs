using System.Security.Cryptography;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// Fleet test data under a unique tag (stage 6): a John Deere tractor model with two plans and a kit part in Fleet's
/// catalogue copy, and active machines at KLP with reading histories. Written as the migrator, like the other seeds.
/// </summary>
internal sealed class FleetSeed(IdentityDatabaseFixture database, string tag)
{
    /// <summary>A part id of this tag's kit, distinct per tag.</summary>
    public long KitPartId { get; } = 9_100_000 + (Convert.ToInt32(tag[2..], 16) % 90_000);

    /// <summary>A John Deere tractor model with an engine plan (500 h / 12 months, one kit part) and a transmission plan.</summary>
    public async Task<int> ModelAsync()
    {
        var partId = KitPartId;
        await database.ExecuteAsync(
            """
            INSERT INTO fleet.part_snapshot (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at)
            VALUES (@part, 'JD', @oem, 'Kit oil filter', 'PCS', true, now()) ON CONFLICT (part_id) DO NOTHING
            """, ("part", (long)partId), ("oem", $"{tag}-OIL"));
        var model = await database.ScalarAsync<int>(
            """
            INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
            SELECT b.id, t.id, @name FROM ref.brand b, ref.machine_type t WHERE b.code = 'JD' AND t.code = 'TRACTOR'
            RETURNING id
            """, ("name", $"Model {tag}"));
        var engine = await database.ScalarAsync<int>(
            "INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name, interval_months) VALUES (@model, 500, @name, 12) RETURNING id",
            ("model", model), ("name", $"Engine {tag}"));
        await database.ExecuteAsync(
            "INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name, interval_months) VALUES (@model, 1500, @name, 24)",
            ("model", model), ("name", $"Transmission {tag}"));
        await database.ExecuteAsync(
            "INSERT INTO fleet.maintenance_plan_part (maintenance_plan_id, part_id, quantity) VALUES (@plan, @part, 1)",
            ("plan", engine), ("part", (long)partId));
        return model;
    }

    /// <summary>An active machine at KLP (normal conditions, commissioned this year) with readings at (days ago, hours).</summary>
    public async Task<long> MachineAsync(int model, string suffix, params (int DaysAgo, decimal Hours)[] readings)
    {
        var machine = await database.ScalarAsync<long>(
            """
            WITH c AS (
                INSERT INTO fleet.customer (customer_code, region_warehouse_id)
                SELECT @code, w.id FROM inventory.warehouse w WHERE w.code = 'KLP' RETURNING id, region_warehouse_id)
            INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year, commissioned_on)
            SELECT @vin, @model, c.id, oc.id, c.region_warehouse_id, 2022, current_date - 30
            FROM c, ref.operating_condition oc WHERE oc.code = 'NORMAL'
            RETURNING id
            """, ("code", $"CUST-{RandomNumberGenerator.GetInt32(0, 1_000_000):D6}"), ("vin", $"{tag}{suffix}0000"), ("model", model));
        foreach (var (daysAgo, hours) in readings)
        {
            await database.ExecuteAsync(
                "INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source) VALUES (@m, @h, now() + make_interval(days => @d), 'import')",
                ("m", machine), ("h", hours), ("d", daysAgo));
        }

        return machine;
    }

    /// <summary>A model whose engine plan's kit is 30 l of oil and one filter, both catalogue parts stocked at KLP.</summary>
    public async Task<(long Machine, long Oil, long Filter)> MachineWithStockedKitAsync(string suffix, decimal oilOnHand, decimal filterOnHand)
    {
        var oil = await StockedPartAsync($"{suffix}-OIL", oilOnHand);
        var filter = await StockedPartAsync($"{suffix}-FLT", filterOnHand);
        var model = await database.ScalarAsync<int>(
            """
            INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
            SELECT b.id, t.id, @name FROM ref.brand b, ref.machine_type t WHERE b.code = 'JD' AND t.code = 'TRACTOR' RETURNING id
            """, ("name", $"Kit model {tag}{suffix}"));
        var plan = await database.ScalarAsync<int>(
            "INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name) VALUES (@model, 500, 'Engine service') RETURNING id", ("model", model));
        await database.ExecuteAsync(
            "INSERT INTO fleet.maintenance_plan_part (maintenance_plan_id, part_id, quantity) VALUES (@plan, @oil, 30), (@plan, @filter, 1)",
            ("plan", plan), ("oil", oil), ("filter", filter));
        return (await MachineAsync(model, suffix, (-20, 440)), oil, filter);
    }

    public async Task<long> StockedPartAsync(string suffix, decimal onHand)
    {
        var part = await database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Kit flow test part', c.id, u.id, 4.20
            FROM ref.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """, ("oem", $"{tag}-{suffix}"));
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id) SELECT w.id, @part FROM inventory.warehouse w WHERE w.code = 'KLP';
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            SELECT now() - interval '1 minute', w.id, @part, 'OPENING_BALANCE', @quantity, 4.20, 'Test stock' FROM inventory.warehouse w WHERE w.code = 'KLP';
            """, ("part", part), ("quantity", onHand));
        return part;
    }
}
