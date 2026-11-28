using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The Inventory aggregates are mapped directly onto the unchanged tables (ADR 0013): the trigger assigns the transfer
/// number, xmin is the concurrency token, and explicit values win over store defaults (#19).
/// </summary>
[Trait("Category", "Integration")]
[Collection(InventoryDatabaseGroup.Name)]
public sealed class InventoryMappingTests(InventoryDatabaseFixture database)
{
    private static readonly Guid Planner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task A_proposed_transfer_gets_its_number_from_the_database_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var (from, to, parts) = await RouteWithPartsAsync(2);
        long id;
        string number;

        await using (var context = database.CreateContext())
        {
            var order = TransferOrder.Propose(from, to, isUrgent: true, Planner,
                [new TransferLineDraft(parts[0], 3), new TransferLineDraft(parts[1], 2.5m, 4, "Half a pallet")]).Value;
            context.Add(order);
            await context.SaveChangesAsync(ct);
            id = order.Id;
            number = order.Number;
        }

        number.ShouldMatch(@"^TR-\d{4}-\d{6}$");
        await using (var context = database.CreateContext())
        {
            var order = await context.Set<TransferOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);
            order.Number.ShouldBe(number);
            order.Status.ShouldBe(TransferStatus.Proposed);
            order.IsUrgent.ShouldBeTrue();
            order.RequestedBy.ShouldBe(Planner);
            order.CreatedAt.ShouldNotBe(default);
            order.Lines.Select(l => (l.PartId, l.Quantity, l.ProposedQty, l.OverrideReason))
                .ShouldBe([(parts[0], 3m, (decimal?)null, (string?)null), (parts[1], 2.5m, 4m, "Half a pallet")], ignoreOrder: true);
            order.Lines.ShouldAllBe(l => l.TransferOrderId == id && l.Id > 0);
        }
    }

    [Fact]
    public async Task Lines_of_a_proposal_are_changed_and_removed_through_the_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (from, to, parts) = await RouteWithPartsAsync(2);
        long id;

        await using (var context = database.CreateContext())
        {
            var order = TransferOrder.Propose(from, to, false, Planner,
                [new TransferLineDraft(parts[0], 3), new TransferLineDraft(parts[1], 1)]).Value;
            context.Add(order);
            await context.SaveChangesAsync(ct);
            id = order.Id;
        }

        await using (var context = database.CreateContext())
        {
            var order = await context.Set<TransferOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);
            order.ChangeLineQuantity(parts[0], 5, null).IsSuccess.ShouldBeTrue();
            order.RemoveLine(parts[1]).IsSuccess.ShouldBeTrue();
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var order = await context.Set<TransferOrder>().Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);
            order.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(5);
        }
    }

    [Fact]
    public async Task A_concurrent_change_of_the_same_transfer_is_detected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (from, to, parts) = await RouteWithPartsAsync(1);
        long id;

        await using (var context = database.CreateContext())
        {
            var order = TransferOrder.Propose(from, to, false, Planner, [new TransferLineDraft(parts[0], 3)]).Value;
            context.Add(order);
            await context.SaveChangesAsync(ct);
            id = order.Id;
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.Set<TransferOrder>().SingleAsync(o => o.Id == id, ct);
        var b = await second.Set<TransferOrder>().SingleAsync(o => o.Id == id, ct);

        // Only a transition that the table grants to the application is written through EF: the route of a proposal.
        a.IsUrgent.ShouldBeFalse();
        first.Entry(a).Property(o => o.IsUrgent).CurrentValue = true;
        await first.SaveChangesAsync(ct);

        second.Entry(b).Property(o => o.IsUrgent).CurrentValue = true;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Stock_levels_are_saved_with_concurrency_and_stocked_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var (warehouse, part) = await FreePlacementAsync();

        await using (var context = database.CreateContext())
        {
            context.Add(WarehousePart.Create(warehouse, part, 2, 10).Value);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var policy = await context.Set<WarehousePart>().SingleAsync(p => p.WarehouseId == warehouse && p.PartId == part, ct);
            policy.IsStocked.ShouldBeTrue();
            policy.SafetyStockQty.ShouldBe(2);
            policy.ChangeStockLevels(3, 12).IsSuccess.ShouldBeTrue();
            await context.SaveChangesAsync(ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.Set<WarehousePart>().SingleAsync(p => p.WarehouseId == warehouse && p.PartId == part, ct);
        var b = await second.Set<WarehousePart>().SingleAsync(p => p.WarehouseId == warehouse && p.PartId == part, ct);
        a.SafetyStockQty.ShouldBe(3);
        a.ChangeStockLevels(4, null).IsSuccess.ShouldBeTrue();
        await first.SaveChangesAsync(ct);
        b.ChangeStockLevels(5, null).IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task An_adjustment_posted_through_EF_moves_the_balance_through_the_ledger_trigger()
    {
        var ct = TestContext.Current.CancellationToken;
        var (warehouse, part) = await FreePlacementAsync();

        await using (var context = database.CreateContext())
        {
            context.Add(WarehousePart.Create(warehouse, part, 0, null).Value);
            await context.SaveChangesAsync(ct);
            var count = await context.Set<AdjustmentReason>().AsNoTracking().SingleAsync(r => r.Code == "COUNT_CORRECTION", ct);
            var damaged = await context.Set<AdjustmentReason>().AsNoTracking().SingleAsync(r => r.Code == "DAMAGED", ct);
            context.Add(StockMovement.Adjust(warehouse, part, 7, 10, count, "Stock count", Planner, DateTimeOffset.UtcNow).Value);
            context.Add(StockMovement.Adjust(warehouse, part, -2, 10, damaged, null, Planner, DateTimeOffset.UtcNow).Value);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var balance = await context.Set<StockBalance>().AsNoTracking().SingleAsync(b => b.WarehouseId == warehouse && b.PartId == part, ct);
            balance.OnHandQty.ShouldBe(5);
            balance.AvailableQty.ShouldBe(5);
            var policy = await context.Set<WarehousePart>().AsNoTracking().SingleAsync(p => p.WarehouseId == warehouse && p.PartId == part, ct);
            balance.ToPosition(policy).Status.ShouldBe(StockStatus.Ok);

            var movements = await context.Set<StockMovement>().AsNoTracking()
                .Where(m => m.WarehouseId == warehouse && m.PartId == part).OrderBy(m => m.Id).ToListAsync(ct);
            movements.Select(m => m.SignedQuantity).ShouldBe([7m, -2m]);
        }
    }

    /// <summary>A route of the seeded network and new parts of the first brand, category and unit.</summary>
    private async Task<(short From, short To, long[] Parts)> RouteWithPartsAsync(int count)
    {
        var from = await database.ScalarAsync<short>("SELECT from_warehouse_id FROM inventory.warehouse_route ORDER BY 1, to_warehouse_id LIMIT 1");
        var to = await database.ScalarAsync<short>($"SELECT to_warehouse_id FROM inventory.warehouse_route WHERE from_warehouse_id = {from} ORDER BY 1 LIMIT 1");
        var parts = new long[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = await NewPartAsync();
        }

        return (from, to, parts);
    }

    private async Task<(short Warehouse, long Part)> FreePlacementAsync() =>
        (await database.ScalarAsync<short>("SELECT min(id) FROM inventory.warehouse"), await NewPartAsync());

    private Task<long> NewPartAsync()
    {
        var oem = $"IT{Guid.NewGuid():N}".ToUpperInvariant();
        return database.ScalarAsync<long>(
            $"""
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT (SELECT min(id) FROM catalog.brand), '{oem}', 'Integration test part',
                   (SELECT min(id) FROM ref.part_category), (SELECT min(id) FROM ref.unit_of_measure), 10
            RETURNING id
            """);
    }
}
