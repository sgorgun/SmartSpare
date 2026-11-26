using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The service-kit reservation as integration events in the single host (STAGE_6_PLAN 5.3, PR 6g2, #12): a reading drafts
/// a service order, <c>ServiceOrderDrafted</c> goes through Fleet's outbox to Inventory, which soft-reserves the kit as the
/// engineer and answers with <c>ServiceKitReserved</c>, which Fleet applies. A redelivered event never reserves twice.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class KitReservationFlowTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("kitflow.engineer", [AppRoles.ServiceEngineer]);

    private readonly string _tag = "KF" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private FleetSeed _seed = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _seed = new FleetSeed(database, _tag);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_drafted_order_gets_its_kit_reserved_by_Inventory_and_becomes_parts_reserved()
    {
        var (machine, oil, filter) = await MachineWithKitAsync("A", oilOnHand: 50, filterOnHand: 5);

        var order = await DraftByReadingAsync(machine);

        await EventuallyAsync(async () => await StatusAsync(order) == "parts_reserved");
        (await LinesAsync(order)).ShouldBe([(oil, 30m, 30m), (filter, 1m, 1m)], "part, required, Fleet's qty_reserved copy (oil was created first)");
        var holds = await database.ScalarAsync<long>(
            """
            SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
            WHERE l.service_order_id = @o AND r.status = 'soft' AND r.created_by = @user
              AND r.expires_at BETWEEN now() + interval '6 days' AND now() + interval '8 days'
            """, ("o", order), ("user", Engineer.Id));
        holds.ShouldBe(2, "soft holds for 7 days, created as the engineer who logged the reading");
        (await database.ScalarAsync<long>("SELECT count(*) FROM fleet.outbox_message WHERE type LIKE '%.ServiceOrderDrafted,%' AND (payload ->> 'serviceOrderId')::bigint = @o", ("o", order)))
            .ShouldBe(1);
        (await database.ScalarAsync<long>("SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.ServiceKitReserved,%' AND (payload ->> 'serviceOrderId')::bigint = @o", ("o", order)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_short_kit_is_held_partially_and_the_order_stays_a_draft()
    {
        var (machine, oil, filter) = await MachineWithKitAsync("B", oilOnHand: 12, filterOnHand: 5);

        var order = await DraftByReadingAsync(machine);

        await EventuallyAsync(async () => (await LinesAsync(order)).Any(l => l.Reserved > 0));
        (await LinesAsync(order)).ShouldBe([(oil, 30m, 12m), (filter, 1m, 1m)]);
        (await StatusAsync(order)).ShouldBe("draft");
        (await database.ScalarAsync<decimal>("SELECT qty_missing FROM fleet.v_service_kit_shortage WHERE service_order_id = @o AND part_id = @p", ("o", order), ("p", oil)))
            .ShouldBe(18m);
    }

    [Fact]
    public async Task A_redelivered_ServiceOrderDrafted_never_reserves_twice()
    {
        var (machine, oil, _) = await MachineWithKitAsync("C", oilOnHand: 50, filterOnHand: 5);
        var order = await DraftByReadingAsync(machine);
        await EventuallyAsync(async () => await StatusAsync(order) == "parts_reserved");
        var (id, type, payload) = await OutboxRowAsync(order);
        var drafted = (ServiceOrderDrafted)IntegrationEventSerializer.Deserialize(type, payload);

        // The same message again (a broker redelivery): the inbox recognises it and the consumer does not run.
        (await DeliverAsync(drafted, id)).ShouldBeFalse();
        // The same event under a new message id (a replay after the inbox was purged): the SQL is idempotent per line.
        (await DeliverAsync(drafted, Guid.NewGuid())).ShouldBeTrue();

        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id WHERE l.service_order_id = @o",
                ("o", order)))
            .ShouldBe(2, "one hold per line");
        (await database.ScalarAsync<decimal>(
                "SELECT reserved_qty FROM inventory.stock_balance b JOIN inventory.warehouse w ON w.id = b.warehouse_id WHERE w.code = 'KLP' AND b.part_id = @p",
                ("p", oil)))
            .ShouldBe(30m);
    }

    private async Task<bool> DeliverAsync(ServiceOrderDrafted drafted, Guid messageId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetServices<IIntegrationEventConsumer<ServiceOrderDrafted>>().ShouldHaveSingleItem();
        return await IntegrationEventDelivery.DeliverAsync(
            scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, drafted, messageId, Ct);
    }

    /// <summary>Logs 512 h on a machine whose 500 h engine service is 60 h away, and returns the drafted order.</summary>
    private async Task<long> DraftByReadingAsync(long machine)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.PostAsync(
            $"/api/v1/fleet/machines/{machine}/readings", new RecordReadingRequest(512, DateTimeOffset.UtcNow.AddMinutes(-5)), bearer: _host.Token(Engineer.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await database.ScalarAsync<long>("SELECT id FROM fleet.service_order WHERE machine_id = @m", ("m", machine));
    }

    /// <summary>A model whose engine plan's kit is 30 l of oil and one filter, both catalogue parts stocked at KLP.</summary>
    private async Task<(long Machine, long Oil, long Filter)> MachineWithKitAsync(string suffix, decimal oilOnHand, decimal filterOnHand)
    {
        var oil = await StockedPartAsync($"{suffix}-OIL", oilOnHand);
        var filter = await StockedPartAsync($"{suffix}-FLT", filterOnHand);
        var model = await database.ScalarAsync<int>(
            """
            INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
            SELECT b.id, t.id, @name FROM ref.brand b, ref.machine_type t WHERE b.code = 'JD' AND t.code = 'TRACTOR' RETURNING id
            """, ("name", $"Kit model {_tag}{suffix}"));
        var plan = await database.ScalarAsync<int>(
            "INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name) VALUES (@model, 500, 'Engine service') RETURNING id", ("model", model));
        await database.ExecuteAsync(
            "INSERT INTO fleet.maintenance_plan_part (maintenance_plan_id, part_id, quantity) VALUES (@plan, @oil, 30), (@plan, @filter, 1)",
            ("plan", plan), ("oil", oil), ("filter", filter));
        return (await _seed.MachineAsync(model, suffix, (-20, 440)), oil, filter);
    }

    private async Task<long> StockedPartAsync(string suffix, decimal onHand)
    {
        var part = await database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Kit flow test part', c.id, u.id, 4.20
            FROM ref.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """, ("oem", $"{_tag}-{suffix}"));
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id) SELECT w.id, @part FROM inventory.warehouse w WHERE w.code = 'KLP';
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            SELECT now() - interval '1 minute', w.id, @part, 'OPENING_BALANCE', @quantity, 4.20, 'Test stock' FROM inventory.warehouse w WHERE w.code = 'KLP';
            """, ("part", part), ("quantity", onHand));
        return part;
    }

    private Task<string> StatusAsync(long order) =>
        database.ScalarAsync<string>("SELECT status::text FROM fleet.service_order WHERE id = @o", ("o", order));

    private async Task<List<(long Part, decimal Required, decimal Reserved)>> LinesAsync(long order)
    {
        var rows = await database.ScalarAsync<string>(
            "SELECT string_agg(part_id || ':' || qty_required || ':' || qty_reserved, ',' ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = @o",
            ("o", order));
        return
        [
            .. rows.Split(',').Select(r => r.Split(':')).Select(p => (long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture))),
        ];
    }

    private async Task<(Guid Id, string Type, string Payload)> OutboxRowAsync(long order)
    {
        var row = await database.ScalarAsync<string>(
            "SELECT id || '|' || type || '|' || payload::text FROM fleet.outbox_message WHERE type LIKE '%.ServiceOrderDrafted,%' AND (payload ->> 'serviceOrderId')::bigint = @o",
            ("o", order));
        var parts = row.Split('|', 3);
        return (Guid.Parse(parts[0]), parts[1], parts[2]);
    }

    /// <summary>The single host delivers through the outbox dispatcher's polling interval; waits up to 30 s.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        (await condition()).ShouldBeTrue("the event flow did not complete within 30 s");
    }
}
