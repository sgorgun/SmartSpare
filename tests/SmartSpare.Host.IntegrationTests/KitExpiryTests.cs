using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Expiry of service-kit holds (STAGE_6_PLAN 5.5, PR 6g3, #12): the job expires the holds overdue at its clock
/// (<see cref="TimeProvider"/>, moved forward here instead of editing data), publishes <c>ServiceKitReservationExpired</c>,
/// and Fleet reopens the order. Expiry racing the consumption of the same holds by a completion expires or consumes each
/// hold, never both, and never fails.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class KitExpiryTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string ExpiryJob = "inventory.reservation-expiry";
    private static readonly TestUser Engineer = TestUser.Create("kitexpiry.engineer", [AppRoles.ServiceEngineer]);

    private readonly string _tag = "KE" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

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
    public async Task Holds_expire_by_the_job_clock_and_Fleet_reopens_the_order()
    {
        var order = await ReservedOrderAsync("A");

        // Six days on, the 7-day holds are still valid.
        _host.Time.Advance(TimeSpan.FromDays(6));
        await RunExpiryAsync();
        (await HoldsAsync(order, "soft")).ShouldBe(2);

        // Eight days on they are overdue: expired, released, and Fleet's order is back to draft without holds.
        _host.Time.Advance(TimeSpan.FromDays(2));
        await RunExpiryAsync();
        (await HoldsAsync(order, "expired")).ShouldBe(2);
        (await database.ScalarAsync<long>(
                """
                SELECT count(*) FROM audit.audit_log a
                WHERE a.table_name = 'inventory.stock_reservation' AND a.action = 'UPDATE' AND a.new_values ->> 'status' = 'expired'
                  AND a.user_id = @system AND a.reason = 'job:inventory.reservation-expiry'
                  AND a.record_id IN (SELECT r.id::text FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
                                      WHERE l.service_order_id = @o)
                """, ("system", SystemUser.Id), ("o", order)))
            .ShouldBe(2, "ADR 0024: the job's writes are attributed to the named system user");
        await EventuallyAsync(async () => await StatusAsync(order) == "draft");
        (await database.ScalarAsync<decimal>("SELECT sum(qty_reserved) FROM fleet.service_order_line WHERE service_order_id = @o", ("o", order))).ShouldBe(0m);
        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.ServiceKitReservationExpired,%' AND payload -> 'lineIds' @> (SELECT to_jsonb(array_agg(id)) FROM fleet.service_order_line WHERE service_order_id = @o)",
                ("o", order)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Expiry_racing_a_completion_expires_or_consumes_each_hold_never_both()
    {
        var orders = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            orders.Add(await ReservedOrderAsync($"R{i}"));
        }

        // One expiry run against four completions at once, at a clock where every hold is overdue.
        _host.Time.Advance(TimeSpan.FromDays(8));
        await Task.WhenAll([RunExpiryAsync(), .. orders.Select(CompleteAsync)]);

        foreach (var order in orders)
        {
            var closed = await database.ScalarAsync<long>(
                """
                SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
                WHERE l.service_order_id = @o AND r.status IN ('expired', 'consumed')
                """, ("o", order));
            closed.ShouldBe(2, "each of the two holds ended exactly once, as expired or consumed");
            (await HoldsAsync(order, "soft")).ShouldBe(0);
            (await StatusAsync(order)).ShouldBe("completed");
        }

        // reserved_qty equals the active holds for every part touched: nothing released twice, nothing left behind.
        (await database.ScalarAsync<long>(
                """
                SELECT count(*) FROM inventory.stock_balance b
                WHERE b.reserved_qty <> (SELECT coalesce(sum(r.quantity), 0) FROM inventory.stock_reservation r
                                         WHERE r.warehouse_id = b.warehouse_id AND r.part_id = b.part_id AND r.status IN ('soft', 'firm'))
                """))
            .ShouldBe(0);
    }

    /// <summary>Runs the job through the <c>--job</c> entry point, so it acts as the named system user (ADR 0024).</summary>
    private async Task RunExpiryAsync() =>
        (await JobRunner.RunAsync(_host.Services, ExpiryJob, Ct)).ShouldBe(JobRunner.Succeeded);

    /// <summary>
    /// The completion and Inventory's consumption of the holds, as the completion command and Inventory's consumer of
    /// ServiceOrderCompleted do it (PR 6h2), in one transaction so the consumption races the expiry job.
    /// </summary>
    private async Task CompleteAsync(long order)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand(
                         "SELECT set_config('app.user_id', @user, true), set_config('app.user_roles', 'SERVICE_ENGINEER', true), set_config('app.user_permissions', '" + string.Join(',', SmartSpare.ApiContracts.Authorization.DefaultRoles.Bundles["SERVICE_ENGINEER"]) + "', true)", connection, transaction))
        {
            context.Parameters.AddWithValue("user", Engineer.Id.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        await using (var complete = new NpgsqlCommand(
                         """
                         SELECT fleet.complete_service_order(@o, 515);
                         SELECT count(*) FROM inventory.issue_service_kit(
                             (SELECT warehouse_id FROM fleet.service_order WHERE id = @o),
                             (SELECT array_agg(id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = @o),
                             (SELECT array_agg(part_id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = @o),
                             (SELECT array_agg(qty_required ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = @o));
                         """, connection, transaction))
        {
            complete.Parameters.AddWithValue("o", order);
            await complete.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    /// <summary>A machine whose reading drafted an engine service; Inventory has soft-reserved its two-part kit at KLP.</summary>
    private async Task<long> ReservedOrderAsync(string suffix)
    {
        var (machine, _, _) = await _seed.MachineWithStockedKitAsync(suffix, oilOnHand: 50, filterOnHand: 5);
        using (var browser = _host.NewBrowser())
        using (var response = await browser.PostAsync(
                   $"/api/v1/fleet/machines/{machine}/readings", new RecordReadingRequest(512, DateTimeOffset.UtcNow.AddMinutes(-5)),
                   bearer: _host.Token(Engineer.Principal())))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        }

        var order = await database.ScalarAsync<long>("SELECT id FROM fleet.service_order WHERE machine_id = @m", ("m", machine));
        await EventuallyAsync(async () => await StatusAsync(order) == "parts_reserved");
        return order;
    }

    private Task<long> HoldsAsync(long order, string status) =>
        database.ScalarAsync<long>(
            """
            SELECT count(*) FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
            WHERE l.service_order_id = @o AND r.status = @s::inventory.reservation_status
            """, ("o", order), ("s", status));

    private Task<string> StatusAsync(long order) =>
        database.ScalarAsync<string>("SELECT status::text FROM fleet.service_order WHERE id = @o", ("o", order));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60 && !await condition(); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        (await condition()).ShouldBeTrue("the event flow did not complete within 30 s");
    }
}
