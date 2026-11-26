using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory;

/// <summary>Stock positions, stocking policy, reservations, adjustments and quantities.</summary>
public sealed class StockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData(0, 0, 2, null, nameof(StockStatus.Out))]
    [InlineData(5, 5, 2, null, nameof(StockStatus.Out))]
    [InlineData(5, 4, 2, null, nameof(StockStatus.BelowSafety))]
    [InlineData(10, 0, 2, null, nameof(StockStatus.Ok))]
    [InlineData(10, 0, 2, 10.0, nameof(StockStatus.Ok))]
    [InlineData(11, 0, 2, 10.0, nameof(StockStatus.AboveMax))]
    [InlineData(3, 0, 0, null, nameof(StockStatus.Ok))]
    public void The_status_compares_the_stock_with_the_policy(double onHand, double reserved, double safety, double? max, string expected)
    {
        var position = new StockPosition((decimal)onHand, (decimal)reserved, (decimal)safety, (decimal?)max);

        position.Status.ShouldBe(Enum.Parse<StockStatus>(expected));
    }

    [Fact]
    public void Available_stock_is_on_hand_minus_reserved()
    {
        var position = new StockPosition(OnHand: 20, Reserved: 6, SafetyStock: 5, MaxStock: 30, InTransit: 4);

        position.Available.ShouldBe(14);
        position.Surplus.ShouldBe(9);
        position.InTransit.ShouldBe(4);
    }

    [Fact]
    public void A_position_below_its_safety_stock_has_no_surplus() =>
        new StockPosition(OnHand: 3, Reserved: 0, SafetyStock: 5, MaxStock: null).Surplus.ShouldBe(0);

    [Theory]
    [InlineData(14, true)]
    [InlineData(14.001, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Issues_never_exceed_the_available_stock(decimal quantity, bool allowed) =>
        new StockPosition(20, 6, 5, null).CanIssue(quantity).ShouldBe(allowed);

    [Theory]
    [InlineData(9, false, true)]     // leaves exactly the safety stock
    [InlineData(10, false, false)]   // would dig into the buffer (SS201)
    [InlineData(10, true, true)]     // urgent orders may use the buffer
    [InlineData(14, true, true)]
    [InlineData(15, true, false)]    // never more than is available (SS200)
    public void A_donor_gives_only_stock_above_its_safety_buffer_unless_urgent(decimal quantity, bool isUrgent, bool allowed) =>
        new StockPosition(20, 6, 5, null).CanGive(quantity, isUrgent).ShouldBe(allowed);

    [Fact]
    public void Stock_levels_keep_the_maximum_above_the_safety_stock()
    {
        var policy = WarehousePart.Create(1, 101, 5, 20).Value;
        policy.IsStocked.ShouldBeTrue();

        policy.ChangeStockLevels(6, 30).IsSuccess.ShouldBeTrue();
        policy.SafetyStockQty.ShouldBe(6);
        policy.MaxStockQty.ShouldBe(30);

        policy.ChangeStockLevels(6, null).IsSuccess.ShouldBeTrue();
        policy.MaxStockQty.ShouldBeNull();

        policy.ChangeStockLevels(10, 9).Error.ShouldBe(InventoryErrors.InvalidStockLevels);
        policy.ChangeStockLevels(-1, null).Error.ShouldBe(InventoryErrors.InvalidStockLevels);
        policy.ChangeStockLevels(1.0001m, null).Error.ShouldBe(InventoryErrors.InvalidStockLevels);
        policy.ChangeStockLevels(1, 2.0001m).Error.ShouldBe(InventoryErrors.InvalidStockLevels);
        policy.SafetyStockQty.ShouldBe(6);

        WarehousePart.Create(1, 101, 5, 4).Error.ShouldBe(InventoryErrors.InvalidStockLevels);
    }

    [Fact]
    public void Zero_safety_stock_is_allowed() =>
        WarehousePart.Create(1, 101, 0, 0).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_position_combines_the_balance_with_the_policy()
    {
        var policy = WarehousePart.Create(1, 101, 5, 20).Value;

        var position = new StockPosition(12, 2, policy.SafetyStockQty, policy.MaxStockQty, 3);

        position.ShouldBe(new StockPosition(12, 2, 5, 20, 3));
    }

    [Fact]
    public void A_firm_reservation_is_consumed_or_released_once()
    {
        var hold = StockReservation.Firm(1, 101, 4, transferOrderLineId: 77, User).Value;
        hold.Status.ShouldBe(ReservationStatus.Firm);
        hold.IsOpen.ShouldBeTrue();
        hold.TransferOrderLineId.ShouldBe(77);

        hold.Expire(Now).Error.Code.ShouldBe("SS300");
        hold.Confirm().Error.Code.ShouldBe("SS300");
        hold.Consume(Now).IsSuccess.ShouldBeTrue();

        hold.Status.ShouldBe(ReservationStatus.Consumed);
        hold.ClosedAt.ShouldBe(Now);
        hold.IsOpen.ShouldBeFalse();
        hold.Release(Now).Error.Code.ShouldBe("SS300");
        hold.Consume(Now).Error.Code.ShouldBe("SS300");
    }

    [Fact]
    public void A_soft_reservation_expires_or_becomes_firm()
    {
        var expiring = StockReservation.Soft(1, 101, 2, serviceOrderLineId: 9, Now.AddDays(7), User).Value;
        expiring.ExpiresAt.ShouldBe(Now.AddDays(7));
        expiring.Expire(Now.AddDays(7)).IsSuccess.ShouldBeTrue();
        expiring.Status.ShouldBe(ReservationStatus.Expired);

        var confirmed = StockReservation.Soft(1, 101, 2, 9, Now.AddDays(7), User).Value;
        confirmed.Confirm().IsSuccess.ShouldBeTrue();
        confirmed.Status.ShouldBe(ReservationStatus.Firm);
        confirmed.Release(Now).IsSuccess.ShouldBeTrue();
        confirmed.Status.ShouldBe(ReservationStatus.Released);
    }

    [Fact]
    public void Reservations_hold_positive_quantities()
    {
        StockReservation.Firm(1, 101, 0, 77, User).Error.ShouldBe(InventoryErrors.InvalidQuantity);
        StockReservation.Soft(1, 101, -2, 9, Now, User).Error.ShouldBe(InventoryErrors.InvalidQuantity);
    }

    [Theory]
    [InlineData(3, MovementTypes.AdjustmentIn, 3)]
    [InlineData(-2.5, MovementTypes.AdjustmentOut, -2.5)]
    public void An_adjustment_posts_its_direction_as_the_movement_type(decimal change, string type, decimal effect)
    {
        var movement = StockMovement.Adjust(1, 101, change, 18.40m, " Stock count 2026-11 ", User, Now).Value;

        movement.MovementTypeCode.ShouldBe(type);
        movement.Quantity.ShouldBe(Math.Abs(change));
        movement.SignedQuantity.ShouldBe(effect);
        movement.Note.ShouldBe("Stock count 2026-11");
        movement.OccurredAt.ShouldBe(Now);
        movement.UnitCost.ShouldBe(18.40m);
        movement.CreatedBy.ShouldBe(User);
        movement.WarehouseId.ShouldBe((short)1);
        movement.PartId.ShouldBe(101);
    }

    [Theory]
    [InlineData(0, "note")]
    [InlineData(0.0001, "note")]
    public void An_adjustment_needs_a_quantity(decimal change, string note) =>
        StockMovement.Adjust(1, 101, change, 1, note, User, Now).Error.ShouldBe(InventoryErrors.InvalidQuantity);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_adjustment_needs_a_note(string? note)
    {
        StockMovement.Adjust(1, 101, 1, 1, note, User, Now).Error.ShouldBe(InventoryErrors.NoteRequired);
        StockMovement.Adjust(1, 101, 1, 1, new string('n', 301), User, Now).Error.ShouldBe(InventoryErrors.NoteRequired);
    }

    [Theory]
    [InlineData(MovementTypes.OpeningBalance, 1, false)]
    [InlineData(MovementTypes.ReceiptPurchaseOrder, 1, false)]
    [InlineData(MovementTypes.ReturnFromCustomer, 1, false)]
    [InlineData(MovementTypes.AdjustmentIn, 1, false)]
    [InlineData(MovementTypes.TransferIn, 1, false)]
    [InlineData(MovementTypes.TransferOut, -1, false)]
    [InlineData(MovementTypes.IssueService, -1, true)]
    [InlineData(MovementTypes.IssueSale, -1, true)]
    [InlineData(MovementTypes.AdjustmentOut, -1, false)]
    [InlineData(MovementTypes.HistoricalDemand, 0, true)]
    public void Movement_types_match_the_reference_data(string code, int sign, bool isDemand)
    {
        MovementTypes.BalanceSign(code).ShouldBe(sign);
        MovementTypes.IsDemand(code).ShouldBe(isDemand);
    }

    [Fact]
    public void Unknown_movement_types_are_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => MovementTypes.BalanceSign("GIFT"));

    [Theory]
    [InlineData(1)]
    [InlineData(0.001)]
    [InlineData(999_999_999.999)]
    public void Valid_quantities(decimal value) => Quantity.Create(value).Value.Value.ShouldBe(value);

    [Theory]
    [InlineData(0)]
    [InlineData(-0.001)]
    [InlineData(0.0005)]
    [InlineData(1_000_000_000)]
    public void Invalid_quantities(decimal value) => Quantity.Create(value).Error.ShouldBe(InventoryErrors.InvalidQuantity);

    [Fact]
    public void Quantities_compare_by_value_whatever_their_scale()
    {
        var five = Quantity.Create(5m).Value;
        var fivePointZero = Quantity.Create(5.000m).Value;

        five.ShouldBe(fivePointZero);
        five.GetHashCode().ShouldBe(fivePointZero.GetHashCode());
        five.ToString().ShouldBe("5");
    }
}
