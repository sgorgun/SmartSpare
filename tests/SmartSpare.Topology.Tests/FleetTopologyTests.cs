using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;

namespace SmartSpare.Topology.Tests;

/// <summary>
/// Fleet in the services topology (STAGE_6_PLAN 9, PR 6g4): the gateway routes <c>/api/v1/fleet</c> to the Fleet service,
/// which reads hub codes from the Inventory service over <c>/internal/v1</c>; and the service-kit reservation travels over
/// RabbitMQ: a reading drafts an order in Fleet, <c>ServiceOrderDrafted</c> reaches Inventory, which reserves the kit, and
/// <c>ServiceKitReserved</c> comes back to Fleet. Each service on its own database login.
/// </summary>
[Trait("Category", "Topology")]
[Collection(TopologyGroup.Name)]
public sealed class FleetTopologyTests(TopologyFixture topology)
{
    private readonly string _tag = "TP" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_gateway_routes_the_fleet_api_to_the_fleet_service()
    {
        await topology.CreateUserAsync("topo.fleet.reader", [AppRoles.Sponsor]);
        using var browser = await SignInAsync("topo.fleet.reader");
        var machine = await MachineWithKitAsync();

        var machines = await browser.GetFromJsonAsync<PagedResponse<MachineSummary>>(new Uri($"/api/v1/fleet/machines?search={_tag}", UriKind.Relative), Ct);
        using var plans = await browser.GetAsync(new Uri("/api/v1/fleet/maintenance-plans", UriKind.Relative), Ct);

        var row = machines!.Items.ShouldHaveSingleItem();
        row.Id.ShouldBe(machine);
        row.WarehouseCode.ShouldBe("KLP", "the Fleet service read the hub code from the Inventory service");
        plans.StatusCode.ShouldBe(HttpStatusCode.OK);
        topology.Forwarded.ShouldContain(f => f.Service == "fleet" && f.Path == "/api/v1/fleet/machines");
    }

    [Fact]
    public async Task A_reading_drafts_an_order_whose_kit_is_reserved_over_rabbitmq()
    {
        await topology.CreateUserAsync("topo.fleet.engineer", [AppRoles.ServiceEngineer]);
        using var browser = await SignInAsync("topo.fleet.engineer");
        var machine = await MachineWithKitAsync();

        using var reading = await browser.PostAsJsonAsync(
            new Uri($"/api/v1/fleet/machines/{machine}/readings", UriKind.Relative), new RecordReadingRequest(512, DateTimeOffset.UtcNow.AddMinutes(-5)), Ct);

        reading.StatusCode.ShouldBe(HttpStatusCode.Created, await reading.Content.ReadAsStringAsync(Ct));
        var order = await topology.ScalarAsync<long>("SELECT id FROM fleet.service_order WHERE machine_id = @m", ("m", machine));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await topology.ScalarAsync<string>("SELECT status::text FROM fleet.service_order WHERE id = @o", ("o", order)) != "parts_reserved"
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        (await topology.ScalarAsync<string>("SELECT status::text FROM fleet.service_order WHERE id = @o", ("o", order))).ShouldBe("parts_reserved");
        (await topology.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id WHERE l.service_order_id = @o AND r.status = 'soft'",
                ("o", order)))
            .ShouldBe(2, "Inventory held both kit parts");
        (await topology.ScalarAsync<long>(
                "SELECT count(*) FROM fleet.outbox_message WHERE type LIKE '%.ServiceOrderDrafted,%' AND processed_at IS NOT NULL AND (payload ->> 'serviceOrderId')::bigint = @o",
                ("o", order)))
            .ShouldBe(1, "Fleet published the draft to the broker");
    }

    /// <summary>A tractor at KLP whose 500 h engine service is 60 h away; its kit (30 l oil, one filter) is stocked at KLP.</summary>
    private async Task<long> MachineWithKitAsync() =>
        await topology.ScalarAsync<long>(
            $"""
            WITH parts AS (
                INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
                SELECT b.id, '{_tag}-' || k.suffix, 'Topology kit part', c.id, u.id, 4.20
                FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u, (VALUES ('OIL'), ('FLT')) k (suffix)
                WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
                RETURNING id, oem_part_number),
            stocked AS (
                INSERT INTO inventory.warehouse_part (warehouse_id, part_id) SELECT w.id, p.id FROM parts p, inventory.warehouse w WHERE w.code = 'KLP'
                RETURNING warehouse_id, part_id),
            opening AS (
                INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
                SELECT now() - interval '1 minute', s.warehouse_id, s.part_id, 'OPENING_BALANCE', 50, 4.20, 'Topology stock' FROM stocked s
                RETURNING part_id),
            model AS (
                INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
                SELECT b.id, t.id, 'Topology {_tag}' FROM catalog.brand b, ref.machine_type t WHERE b.code = 'JD' AND t.code = 'TRACTOR'
                RETURNING id),
            plan AS (
                INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name) SELECT id, 500, 'Engine service' FROM model RETURNING id),
            kit AS (
                INSERT INTO fleet.maintenance_plan_part (maintenance_plan_id, part_id, quantity)
                SELECT plan.id, parts.id, CASE WHEN parts.oem_part_number LIKE '%-OIL' THEN 30 ELSE 1 END FROM plan, parts
                RETURNING maintenance_plan_id),
            customer AS (
                INSERT INTO fleet.customer (customer_code, region_warehouse_id)
                SELECT 'CUST-' || lpad((floor(random() * 1000000))::int::text, 6, '0'), w.id FROM inventory.warehouse w WHERE w.code = 'KLP'
                RETURNING id, region_warehouse_id),
            machine AS (
                INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year)
                SELECT '{_tag}MACH0001', model.id, customer.id, oc.id, customer.region_warehouse_id, 2022
                FROM model, customer, ref.operating_condition oc WHERE oc.code = 'NORMAL'
                RETURNING id)
            INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source)
            SELECT id, 440, now() - interval '20 days', 'import' FROM machine
            RETURNING machine_id
            """);

    private Task<HttpClient> SignInAsync(string userName) => topology.SignInAsync(userName);
}
