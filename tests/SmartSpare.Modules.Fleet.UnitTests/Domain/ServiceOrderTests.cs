using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The service order state machine and line rules. They mirror <c>fleet.trg_service_order_status_guard</c> and the
/// CHECKs of <c>fleet.service_order(_line)</c>.
/// </summary>
public sealed class ServiceOrderTests
{
    private const short Pnv = 2;
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private static readonly ServiceOrderLineDraft[] Kit =
        [new(101, 1), new(102, 1), new(103, 30)];

    private static ServiceOrder Draft() =>
        ServiceOrder.DraftFromPlan(machineId: 1, planId: 5, Pnv, dueEngineHours: 500, triggerEngineHours: 512, Engineer, Kit).Value;

    private static ServiceOrder Repair() =>
        ServiceOrder.DraftRepair(machineId: 1, Pnv, machineEngineHours: 512, Engineer, [new(201, 2)]).Value;

    private static ServiceOrder InProgress()
    {
        var order = Draft();
        order.Start().IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact]
    public void A_planned_draft_carries_the_kit_threshold_and_trigger_hours()
    {
        var order = Draft();

        order.Status.ShouldBe(ServiceOrderStatus.Draft);
        order.IsPlanned.ShouldBeTrue();
        order.IsOpen.ShouldBeTrue();
        order.MaintenancePlanId.ShouldBe(5);
        order.DueEngineHours.ShouldBe(500);
        order.TriggerEngineHours.ShouldBe(512);
        order.WarehouseId.ShouldBe(Pnv);
        order.CreatedBy.ShouldBe(Engineer);
        order.Lines.Select(l => (l.PartId, l.QtyRequired, l.QtyIssued)).ShouldBe([(101L, 1m, 0m), (102L, 1m, 0m), (103L, 30m, 0m)]);
        order.Lines.ShouldAllBe(l => l.QtyOpen == l.QtyRequired);
        order.Lines.ShouldAllBe(l => l.QtyReserved == 0 && l.QtyMissing == l.QtyRequired, "nothing is held until Inventory reserves the kit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(100000.5)]
    public void A_planned_draft_needs_a_valid_positive_threshold(double due)
    {
        ServiceOrder.DraftFromPlan(1, 5, Pnv, (decimal)due, 512, Engineer, Kit).Error.ShouldBe(FleetErrors.InvalidEngineHours);
    }

    [Fact]
    public void A_plan_without_a_kit_drafts_an_order_without_lines()
    {
        ServiceOrder.DraftFromPlan(1, 5, Pnv, 500, 512, Engineer, []).Value.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void A_repair_has_no_plan_and_needs_lines()
    {
        var order = Repair();
        order.IsPlanned.ShouldBeFalse();
        order.DueEngineHours.ShouldBeNull();
        order.TriggerEngineHours.ShouldBe(512);

        ServiceOrder.DraftRepair(1, Pnv, 512, Engineer, []).Error.ShouldBe(FleetErrors.NoLines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.0005)]
    public void A_line_needs_a_positive_quantity_with_at_most_three_decimals(double quantity)
    {
        ServiceOrder.DraftRepair(1, Pnv, 512, Engineer, [new(201, (decimal)quantity)]).Error.ShouldBe(FleetErrors.InvalidQuantity);
    }

    [Fact]
    public void A_part_appears_once()
    {
        ServiceOrder.DraftRepair(1, Pnv, 512, Engineer, [new(201, 1), new(201, 2)]).Error.ShouldBe(FleetErrors.DuplicatePart);
        Draft().AddLine(new(101, 1)).Error.ShouldBe(FleetErrors.DuplicatePart);
    }

    [Fact]
    public void Lines_change_while_the_order_has_not_started()
    {
        var order = Draft();

        order.AddLine(new(104, 2)).IsSuccess.ShouldBeTrue();
        order.ChangeLineQuantity(103, 32).IsSuccess.ShouldBeTrue();
        order.RemoveLine(101).IsSuccess.ShouldBeTrue();

        order.Lines.Select(l => (l.PartId, l.QtyRequired)).ShouldBe([(102L, 1m), (103L, 32m), (104L, 2m)]);
        order.ChangeLineQuantity(999, 1).Error.ShouldBe(FleetErrors.LineNotFound);
        order.RemoveLine(999).Error.ShouldBe(FleetErrors.LineNotFound);
        order.ChangeLineQuantity(103, 0).Error.ShouldBe(FleetErrors.InvalidQuantity);
    }

    [Fact]
    public void A_repair_keeps_its_last_line_but_a_planned_order_may_lose_all()
    {
        Repair().RemoveLine(201).Error.ShouldBe(FleetErrors.NoLines);

        var planned = ServiceOrder.DraftFromPlan(1, 5, Pnv, 500, 512, Engineer, [new(101, 1)]).Value;
        planned.RemoveLine(101).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Lines_are_fixed_once_the_work_has_started()
    {
        var order = InProgress();

        order.AddLine(new(104, 1)).Error.Code.ShouldBe("SS300");
        order.ChangeLineQuantity(101, 2).Error.Code.ShouldBe("SS300");
        order.RemoveLine(101).Error.Description.ShouldBe("A service order that is in progress cannot be changed.");
    }

    [Fact]
    public void A_draft_is_started()
    {
        var order = Draft();

        order.Start().IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.InProgress);
        order.IsOpen.ShouldBeTrue();
        order.Start().Error.Code.ShouldBe("SS300");
    }

    [Fact]
    public void An_open_order_is_completed_with_the_engine_hours()
    {
        var order = InProgress();

        order.Complete(515, machineEngineHours: 512, Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.Completed);
        order.CompletedEngineHours.ShouldBe(515);
        order.CompletedAt.ShouldBe(Now);
        order.IsOpen.ShouldBeFalse();
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ServiceOrderCompletedDomainEvent>();
    }

    [Fact]
    public void A_draft_can_be_completed_directly()
    {
        Draft().Complete(512, 512, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(511, 500)]
    [InlineData(512, 520)]
    public void Completion_hours_are_not_below_the_trigger_or_the_machine(int completedAt, int machineHours)
    {
        var order = Draft();

        order.Complete(completedAt, machineHours, Now).Error.ShouldBe(FleetErrors.CompletionHoursTooLow);
        order.Status.ShouldBe(ServiceOrderStatus.Draft);
    }

    [Fact]
    public void Completion_hours_must_be_valid_engine_hours()
    {
        Draft().Complete(512.55m, 512, Now).Error.ShouldBe(FleetErrors.InvalidEngineHours);
    }

    [Fact]
    public void An_open_order_is_cancelled_with_a_trimmed_reason()
    {
        var order = Draft();

        order.Cancel("  customer sold the machine ", Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.Cancelled);
        order.CancelReason.ShouldBe("customer sold the machine");
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ServiceOrderCancelledDomainEvent>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_cancellation_needs_a_reason(string? reason)
    {
        Draft().Cancel(reason, Now).Error.ShouldBe(FleetErrors.CancelReasonRequired);
    }

    [Fact]
    public void A_cancellation_reason_has_at_most_300_characters()
    {
        Draft().Cancel(new string('x', 301), Now).Error.ShouldBe(FleetErrors.CancelReasonRequired);
        Draft().Cancel(new string('x', 300), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Closed_orders_accept_no_further_step()
    {
        var completed = Draft();
        completed.Complete(512, 512, Now).IsSuccess.ShouldBeTrue();
        var cancelled = Draft();
        cancelled.Cancel("duplicate", Now).IsSuccess.ShouldBeTrue();

        foreach (var order in new[] { completed, cancelled })
        {
            order.Start().Error.Code.ShouldBe("SS300");
            order.Complete(600, 512, Now).Error.Code.ShouldBe("SS300");
            order.Cancel("again", Now).Error.Code.ShouldBe("SS300");
            order.AddLine(new(104, 1)).Error.Code.ShouldBe("SS300");
        }

        completed.Cancel("again", Now).Error.Description.ShouldBe("A service order that is completed cannot be cancelled.");
    }

    [Fact]
    public void The_effective_interval_rounds_like_the_database()
    {
        var plan = new MaintenancePlan(5, 7, "Engine service", 500, 12, [new MaintenancePlanPart(5, 101, 1)]);

        plan.EffectiveInterval(1.0m).ShouldBe(500.0m);
        plan.EffectiveInterval(0.8m).ShouldBe(400.0m);
        plan.EffectiveInterval(0.85m).ShouldBe(425.0m);
        new MaintenancePlan(6, 7, "Hydraulic", 333).EffectiveInterval(0.85m).ShouldBe(283.1m);   // 283.05 rounds away from zero
        plan.IntervalMonths.ShouldBe((short)12);
        plan.Parts.ShouldHaveSingleItem().Quantity.ShouldBe(1);
    }

    [Fact]
    public void A_plan_rejects_intervals_outside_its_checks()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MaintenancePlan(1, 7, "x", 5));
        Should.Throw<ArgumentOutOfRangeException>(() => new MaintenancePlan(1, 7, "x", 500, 61));
        Should.Throw<ArgumentOutOfRangeException>(() => new MaintenancePlan(1, 7, "x", 500).EffectiveInterval(0));
    }
}
