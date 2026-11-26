using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The transfer order state machine and line rules. They mirror <c>trg_transfer_order_status_guard</c>,
/// <c>trg_transfer_line_editable</c> and the CHECKs of <c>inventory.transfer_order(_line)</c>.
/// </summary>
public sealed class TransferOrderTests
{
    private const short Klp = 1;
    private const short Pnv = 2;
    private static readonly Guid Planner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Keeper = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private static TransferOrder Proposed(bool isUrgent = false, params TransferLineDraft[] lines) =>
        TransferOrder.Propose(Klp, Pnv, isUrgent, Planner, lines.Length == 0 ? [new TransferLineDraft(101, 11)] : lines).Value;

    private static TransferOrder Approved()
    {
        var order = Proposed();
        order.Approve(Planner, Now).IsSuccess.ShouldBeTrue();
        return order;
    }

    private static TransferOrder Dispatched()
    {
        var order = Approved();
        order.Dispatch(Keeper, Klp, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact]
    public void A_proposal_starts_proposed_with_its_lines_and_no_number_yet()
    {
        var order = Proposed(false, new TransferLineDraft(101, 11), new TransferLineDraft(102, 2.5m));

        order.Status.ShouldBe(TransferStatus.Proposed);
        order.FromWarehouseId.ShouldBe(Klp);
        order.ToWarehouseId.ShouldBe(Pnv);
        order.RequestedBy.ShouldBe(Planner);
        order.Lines.Select(l => (l.PartId, l.Quantity)).ShouldBe([(101L, 11m), (102L, 2.5m)]);
        order.PartIds.ShouldBe(new HashSet<long> { 101, 102 }, ignoreOrder: true);
        order.Number.ShouldBeNull();
    }

    [Fact]
    public void A_transfer_needs_two_different_warehouses() =>
        TransferOrder.Propose(Klp, Klp, false, Planner, [new TransferLineDraft(101, 1)]).Error.ShouldBe(InventoryErrors.SameWarehouse);

    [Fact]
    public void A_transfer_needs_lines() =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, []).Error.ShouldBe(InventoryErrors.NoLines);

    [Fact]
    public void A_part_appears_once() =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 1), new TransferLineDraft(101, 2)])
            .Error.ShouldBe(InventoryErrors.DuplicatePart);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.0001)]
    [InlineData(1_000_000_000)]
    public void Line_quantities_are_positive_with_three_decimals(decimal quantity) =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, quantity)]).Error.ShouldBe(InventoryErrors.InvalidQuantity);

    [Fact]
    public void A_line_needs_a_part() =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(0, 1)]).Error.ShouldBe(InventoryErrors.InvalidQuantity);

    [Fact]
    public void A_proposed_quantity_must_be_positive() =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 1, ProposedQty: 0)]).Error.ShouldBe(InventoryErrors.InvalidQuantity);

    [Fact]
    public void A_quantity_that_differs_from_the_proposal_needs_a_reason()
    {
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 8, ProposedQty: 11)])
            .Error.ShouldBe(InventoryErrors.OverrideReasonRequired);
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 8, 11, "   ")])
            .Error.ShouldBe(InventoryErrors.OverrideReasonRequired);
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 8, 11, new string('x', 301))])
            .Error.ShouldBe(InventoryErrors.OverrideReasonRequired);

        var accepted = TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 8, 11, " Truck is full ")]).Value;
        accepted.Lines[0].OverrideReason.ShouldBe("Truck is full");
    }

    [Fact]
    public void The_proposed_quantity_itself_needs_no_reason() =>
        TransferOrder.Propose(Klp, Pnv, false, Planner, [new TransferLineDraft(101, 11, ProposedQty: 11)]).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Lines_of_a_proposal_can_be_added_changed_and_removed()
    {
        var order = Proposed();

        order.AddLine(new TransferLineDraft(102, 3)).IsSuccess.ShouldBeTrue();
        order.ChangeLineQuantity(102, 4, null).IsSuccess.ShouldBeTrue();
        order.RemoveLine(101).IsSuccess.ShouldBeTrue();

        order.Lines.Single().PartId.ShouldBe(102);
        order.Lines.Single().Quantity.ShouldBe(4);
    }

    [Fact]
    public void Line_changes_report_unknown_parts_and_keep_the_last_line()
    {
        var order = Proposed();

        order.ChangeLineQuantity(999, 1, null).Error.ShouldBe(InventoryErrors.LineNotFound);
        order.RemoveLine(999).Error.ShouldBe(InventoryErrors.LineNotFound);
        order.RemoveLine(101).Error.ShouldBe(InventoryErrors.NoLines);
        order.AddLine(new TransferLineDraft(101, 1)).Error.ShouldBe(InventoryErrors.DuplicatePart);
    }

    [Fact]
    public void Changing_an_overridden_line_keeps_its_reason_unless_a_new_one_is_given()
    {
        var order = Proposed(false, new TransferLineDraft(101, 8, 11, "Truck is full"));

        order.ChangeLineQuantity(101, 9, null).IsSuccess.ShouldBeTrue();
        order.Lines[0].OverrideReason.ShouldBe("Truck is full");

        order.ChangeLineQuantity(101, 7, "Half a pallet").IsSuccess.ShouldBeTrue();
        order.Lines[0].OverrideReason.ShouldBe("Half a pallet");

        order.ChangeLineQuantity(101, 0, null).Error.ShouldBe(InventoryErrors.InvalidQuantity);
    }

    [Fact]
    public void A_line_without_a_proposal_cannot_lose_its_reason_requirement_by_change()
    {
        var order = Proposed(false, new TransferLineDraft(101, 11, ProposedQty: 11));

        order.ChangeLineQuantity(101, 10, null).Error.ShouldBe(InventoryErrors.OverrideReasonRequired);
        order.Lines[0].Quantity.ShouldBe(11);
    }

    [Fact]
    public void Approval_records_the_approver_and_raises_an_event()
    {
        var order = Proposed();

        order.Approve(Planner, Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(TransferStatus.Approved);
        order.ApprovedBy.ShouldBe(Planner);
        order.ApprovedAt.ShouldBe(Now);
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<TransferApprovedDomainEvent>().OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Dispatch_is_done_by_the_donor_keeper()
    {
        var order = Approved();

        order.Dispatch(Keeper, Pnv, Now).Error.ShouldBe(InventoryErrors.NotYourWarehouse);
        order.Dispatch(Keeper, Klp, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(TransferStatus.Dispatched);
        order.DispatchedBy.ShouldBe(Keeper);
        order.DispatchedAt.ShouldBe(Now.AddHours(1));
    }

    [Fact]
    public void A_user_without_a_home_warehouse_may_dispatch_and_receive()
    {
        var order = Approved();

        order.Dispatch(Keeper, null, Now).IsSuccess.ShouldBeTrue();
        order.Receive(Keeper, null, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Receipt_is_done_by_the_recipient_keeper_and_never_before_the_dispatch()
    {
        var order = Dispatched();

        order.Receive(Keeper, Klp, Now).Error.ShouldBe(InventoryErrors.NotYourWarehouse);
        order.Receive(Keeper, Pnv, Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(TransferStatus.Received);
        order.ReceivedBy.ShouldBe(Keeper);
        order.ReceivedAt.ShouldBe(order.DispatchedAt);   // the clock was behind the dispatch time
    }

    [Fact]
    public void Receipt_after_the_dispatch_keeps_its_own_time()
    {
        var order = Dispatched();

        order.Receive(Keeper, Pnv, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        order.ReceivedAt.ShouldBe(Now.AddDays(1));
    }

    [Theory]
    [InlineData(nameof(TransferStatus.Proposed))]
    [InlineData(nameof(TransferStatus.Approved))]
    public void Proposed_and_approved_orders_can_be_cancelled_with_a_reason(string state)
    {
        var status = Enum.Parse<TransferStatus>(state);
        var order = status == TransferStatus.Proposed ? Proposed() : Approved();
        order.ClearDomainEvents();

        order.Cancel("  Customer cancelled  ", Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(TransferStatus.Cancelled);
        order.CancelReason.ShouldBe("Customer cancelled");
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<TransferCancelledDomainEvent>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_cancellation_needs_a_reason(string? reason)
    {
        var order = Proposed();

        order.Cancel(reason, Now).Error.ShouldBe(InventoryErrors.CancelReasonRequired);
        order.Cancel(new string('x', 301), Now).Error.ShouldBe(InventoryErrors.CancelReasonRequired);
        order.Status.ShouldBe(TransferStatus.Proposed);
    }

    public static TheoryData<string, string> RefusedSteps => new()
    {
        { nameof(TransferStatus.Proposed), "dispatch" },
        { nameof(TransferStatus.Proposed), "receive" },
        { nameof(TransferStatus.Approved), "approve" },
        { nameof(TransferStatus.Approved), "receive" },
        { nameof(TransferStatus.Dispatched), "approve" },
        { nameof(TransferStatus.Dispatched), "dispatch" },
        { nameof(TransferStatus.Dispatched), "cancel" },
        { nameof(TransferStatus.Received), "approve" },
        { nameof(TransferStatus.Received), "dispatch" },
        { nameof(TransferStatus.Received), "receive" },
        { nameof(TransferStatus.Received), "cancel" },
        { nameof(TransferStatus.Cancelled), "approve" },
        { nameof(TransferStatus.Cancelled), "dispatch" },
        { nameof(TransferStatus.Cancelled), "receive" },
        { nameof(TransferStatus.Cancelled), "cancel" },
    };

    [Theory]
    [MemberData(nameof(RefusedSteps))]
    public void Steps_outside_the_state_machine_are_refused_with_SS300(string state, string step)
    {
        var status = Enum.Parse<TransferStatus>(state);
        var order = InStatus(status);

        var result = step switch
        {
            "approve" => order.Approve(Planner, Now),
            "dispatch" => order.Dispatch(Keeper, null, Now),
            "receive" => order.Receive(Keeper, null, Now),
            _ => order.Cancel("reason", Now),
        };

        result.Error.Code.ShouldBe("SS300");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        order.Status.ShouldBe(status);
    }

    [Theory]
    [InlineData(nameof(TransferStatus.Approved))]
    [InlineData(nameof(TransferStatus.Dispatched))]
    [InlineData(nameof(TransferStatus.Received))]
    [InlineData(nameof(TransferStatus.Cancelled))]
    public void Lines_change_only_while_proposed(string state)
    {
        var order = InStatus(Enum.Parse<TransferStatus>(state));

        order.AddLine(new TransferLineDraft(555, 1)).Error.Code.ShouldBe("SS300");
        order.ChangeLineQuantity(101, 2, null).Error.Code.ShouldBe("SS300");
        order.RemoveLine(101).Error.Code.ShouldBe("SS300");
    }

    [Fact]
    public void The_transition_error_names_the_state_and_the_step() =>
        Approved().Approve(Planner, Now).Error.Description.ShouldBe("A transfer order that is approved cannot be approved.");

    private static TransferOrder InStatus(TransferStatus status)
    {
        switch (status)
        {
            case TransferStatus.Proposed:
                return Proposed();
            case TransferStatus.Approved:
                return Approved();
            case TransferStatus.Dispatched:
                return Dispatched();
            case TransferStatus.Received:
                var received = Dispatched();
                received.Receive(Keeper, null, Now.AddDays(1)).IsSuccess.ShouldBeTrue();
                return received;
            default:
                var cancelled = Proposed();
                cancelled.Cancel("reason", Now).IsSuccess.ShouldBeTrue();
                return cancelled;
        }
    }
}
