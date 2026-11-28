using Npgsql;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// <c>inventory.reserve_service_kit</c> under concurrency (STAGE_6_PLAN 5.3, 5.7): two kits that share parts, listed in
/// opposite orders, are reserved at the same time again and again. The function locks the balances in
/// (part_id, warehouse_id) order, so neither transaction ever waits on the other in the opposite order (no deadlock,
/// SQLSTATE 40P01), and the stock invariant 0 &lt;= reserved &lt;= on hand holds throughout.
/// </summary>
[Trait("Category", "Integration")]
[Collection(InventoryDatabaseGroup.Name)]
public sealed class ServiceKitReservationTests(InventoryDatabaseFixture database)
{
    private const int Rounds = 25;
    private const decimal OnHand = 10;
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_kits_reserved_concurrently_never_deadlock_and_never_exceed_the_stock()
    {
        var (warehouse, parts) = await StockedPartsAsync(3);
        var reversed = parts.Reverse().ToArray();
        long nextLine = 9_200_000_000 + (Random.Shared.Next(1, 1000) * 1000L);

        for (var round = 0; round < Rounds; round++)
        {
            var first = Enumerable.Range(0, parts.Length).Select(_ => nextLine++).ToArray();
            var second = Enumerable.Range(0, parts.Length).Select(_ => nextLine++).ToArray();

            // Both kits ask for 3 of every part; the stock runs out after a few rounds, so later kits are partial.
            await Task.WhenAll(ReserveAsync(warehouse, first, parts), ReserveAsync(warehouse, second, reversed));
        }

        foreach (var part in parts)
        {
            var (onHand, reserved, held) = await BalanceAsync(warehouse, part);
            onHand.ShouldBe(OnHand);
            reserved.ShouldBe(held, "reserved_qty equals the sum of the active holds");
            reserved.ShouldBe(OnHand, "every unit ends up held, none twice");
        }
    }

    [Fact]
    public async Task A_kit_is_reserved_partially_and_only_once_per_line()
    {
        var (warehouse, parts) = await StockedPartsAsync(2);
        long[] lines = [9_300_000_001 + Random.Shared.Next(1, 100_000) * 10L, 9_300_000_002 + Random.Shared.Next(1, 100_000) * 10L];

        var reserved = await ReserveAsync(warehouse, lines, parts, quantities: [4, OnHand + 5]);
        var again = await ReserveAsync(warehouse, lines, parts, quantities: [4, OnHand + 5]);

        reserved.ShouldBe(new Dictionary<long, decimal> { [lines[0]] = 4, [lines[1]] = OnHand });
        again.ShouldBe(reserved, "a second call for the same lines reports the holds and adds none");
        (await BalanceAsync(warehouse, parts[0])).Reserved.ShouldBe(4);
        (await BalanceAsync(warehouse, parts[1])).Reserved.ShouldBe(OnHand);
    }

    private async Task<Dictionary<long, decimal>> ReserveAsync(short warehouse, long[] lines, long[] parts, decimal[]? quantities = null)
    {
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("user", Engineer.ToString());
            await context.ExecuteNonQueryAsync(Ct);
        }

        var result = new Dictionary<long, decimal>();
        await using (var reserve = new NpgsqlCommand(
                         "SELECT service_order_line_id, reserved_qty FROM inventory.reserve_service_kit(@warehouse, @lines, @parts, @quantities, now() + interval '7 days')",
                         connection, transaction))
        {
            reserve.Parameters.AddWithValue("warehouse", warehouse);
            reserve.Parameters.AddWithValue("lines", lines);
            reserve.Parameters.AddWithValue("parts", parts);
            reserve.Parameters.AddWithValue("quantities", quantities ?? [.. parts.Select(_ => 3m)]);
            await using var reader = await reserve.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                result[reader.GetInt64(0)] = reader.GetDecimal(1);
            }
        }

        // Keep the locks a moment so the two transactions overlap.
        await using (var pause = new NpgsqlCommand("SELECT pg_sleep(0.02)", connection, transaction))
        {
            await pause.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        return result;
    }

    /// <summary>Parts with an opening balance of <see cref="OnHand"/> at KLP.</summary>
    private async Task<(short Warehouse, long[] Parts)> StockedPartsAsync(int count)
    {
        var tag = "KR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var warehouse = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'KLP'");
        var parts = new long[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = await database.ScalarAsync<long>(
                $"""
                WITH p AS (
                    INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
                    SELECT b.id, '{tag}-{i}', 'Kit reservation test part', c.id, u.id, 1
                    FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
                    WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
                    RETURNING id),
                wp AS (INSERT INTO inventory.warehouse_part (warehouse_id, part_id) SELECT {warehouse}, id FROM p RETURNING part_id),
                m AS (INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
                      SELECT now() - interval '1 minute', {warehouse}, part_id, 'OPENING_BALANCE', {OnHand}, 1, 'Test stock' FROM wp RETURNING part_id)
                SELECT part_id FROM m
                """);
        }

        return (warehouse, parts);
    }

    private async Task<(decimal OnHand, decimal Reserved, decimal Held)> BalanceAsync(short warehouse, long part)
    {
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT b.on_hand_qty, b.reserved_qty,
                   (SELECT coalesce(sum(r.quantity), 0) FROM inventory.stock_reservation r
                    WHERE r.warehouse_id = b.warehouse_id AND r.part_id = b.part_id AND r.status IN ('soft', 'firm'))
            FROM inventory.stock_balance b WHERE b.warehouse_id = @w AND b.part_id = @p
            """, connection);
        command.Parameters.AddWithValue("w", warehouse);
        command.Parameters.AddWithValue("p", part);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
    }
}
