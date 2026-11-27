using System.Reflection;
using System.Text.Json;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Features.ServiceOrders;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// Editing the kit of an open order (PR 6i, #140): what is removed, added and replaced, what the kit status does, and the
/// rules (each part once, a repair keeps a line, fixed once the work started).
/// </summary>
public sealed class KitLineEditingTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A planned draft with an oil filter (line 71, 1 PCS) and oil (line 72, 30 L), both fully held.</summary>
    private static ServiceOrder Reserved()
    {
        var order = ServiceOrder.DraftFromPlan(501, 21, 5, 500, 512, Engineer, [new(1001, 1), new(1002, 30)]).Value;
        SetId(order, 9001);
        SetId(order.Lines[0], 71);
        SetId(order.Lines[1], 72);
        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 1, [72] = 30 }, Now).ShouldBeTrue();
        order.Status.ShouldBe(ServiceOrderStatus.PartsReserved);
        return order;
    }

    private static void SetId<TId>(Entity<TId> entity, TId id)
        where TId : notnull =>
        typeof(Entity<TId>).GetProperty(nameof(Entity<TId>.Id), BindingFlags.Public | BindingFlags.Instance)!.SetValue(entity, id);

    [Fact]
    public void A_new_part_is_added_and_the_kit_is_reserving_again()
    {
        var order = Reserved();

        var change = order.ReplaceLines([new(1001, 1), new(1002, 30), new(1003, 2)]).Value;

        change.RemovedLineIds.ShouldBeEmpty();
        change.AddedLines.ShouldHaveSingleItem().PartId.ShouldBe(1003);
        (order.Status, order.KitReservedAt, order.Kit).ShouldBe((ServiceOrderStatus.Draft, (DateTimeOffset?)null, KitStatus.Reserving));
    }

    [Fact]
    public void A_changed_quantity_replaces_the_line_so_its_hold_is_released_and_reserved_anew()
    {
        var order = Reserved();

        var change = order.ReplaceLines([new(1001, 1), new(1002, 25)]).Value;

        change.RemovedLineIds.ShouldBe([72L]);
        change.AddedLines.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            l => l.PartId.ShouldBe(1002),
            l => l.QtyRequired.ShouldBe(25),
            l => l.QtyReserved.ShouldBe(0));
        order.Lines.Select(l => l.PartId).Order().ShouldBe([1001L, 1002L]);
        order.Kit.ShouldBe(KitStatus.Reserving);
    }

    [Fact]
    public void Removing_the_short_line_of_a_draft_leaves_a_fully_held_kit()
    {
        var order = ServiceOrder.DraftFromPlan(501, 21, 5, 500, 512, Engineer, [new(1001, 1), new(1002, 30)]).Value;
        SetId(order.Lines[0], 71);
        SetId(order.Lines[1], 72);
        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 1, [72] = 12 }, Now);
        order.Kit.ShouldBe(KitStatus.Partial);

        var change = order.ReplaceLines([new(1001, 1)]).Value;

        change.RemovedLineIds.ShouldBe([72L]);
        change.AddedLines.ShouldBeEmpty();
        (order.Status, order.Kit).ShouldBe((ServiceOrderStatus.PartsReserved, KitStatus.Reserved));
    }

    [Fact]
    public void The_same_kit_again_changes_nothing()
    {
        var order = Reserved();

        order.ReplaceLines([new(1002, 30), new(1001, 1)]).Value.IsEmpty.ShouldBeTrue();

        (order.Status, order.Kit).ShouldBe((ServiceOrderStatus.PartsReserved, KitStatus.Reserved));
    }

    [Fact]
    public void A_planned_service_may_lose_all_lines_but_a_repair_keeps_one()
    {
        var planned = Reserved();
        planned.ReplaceLines([]).Value.RemovedLineIds.Count.ShouldBe(2);
        planned.Kit.ShouldBe(KitStatus.NoParts);

        var repair = ServiceOrder.DraftRepair(501, 5, 512, Engineer, [new(1001, 1)]).Value;
        repair.ReplaceLines([]).Error.ShouldBe(FleetErrors.NoLines);
    }

    [Fact]
    public void Each_part_once_and_valid_quantities()
    {
        Reserved().ReplaceLines([new(1001, 1), new(1001, 2)]).Error.ShouldBe(FleetErrors.DuplicatePart);
        Reserved().ReplaceLines([new(1001, 0.0001m)]).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void The_kit_is_fixed_once_the_work_started()
    {
        var order = Reserved();
        order.Start();

        order.ReplaceLines([new(1001, 2)]).Error.Code.ShouldBe("SS300");
    }

    [Fact]
    public void The_event_names_the_removed_lines_and_the_new_lines_with_their_ids()
    {
        var order = Reserved();
        var change = order.ReplaceLines([new(1001, 1), new(1002, 25)]).Value;
        SetId(change.AddedLines[0], 73);

        var changed = ServiceOrderEvents.LinesChanged(order, change, Engineer, Now) with { OccurredAt = Now };

        (changed.ServiceOrderId, changed.WarehouseId, changed.ActorId).ShouldBe((9001L, (short)5, Engineer));
        changed.RemovedLineIds.ShouldBe([72L]);
        changed.AddedLines.ShouldBe([new ServiceOrderKitLine(73, 1002, 25)]);
        var message = IntegrationEventSerializer.ToOutboxMessage(changed);
        IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBeAssignableTo<IActedByUser>()!.ActorId.ShouldBe(Engineer);
        JsonDocument.Parse(message.Payload).RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["id", "occurredAt", "serviceOrderId", "warehouseId", "addedLines", "removedLineIds", "actorId", "changedAt"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(51, false)]
    public void The_request_allows_an_empty_kit_and_at_most_fifty_lines(int lines, bool valid) =>
        new ChangeServiceOrderLinesRequestValidator()
            .Validate(new ChangeServiceOrderLinesRequest([.. Enumerable.Range(1, lines).Select(i => new CreateServiceOrderLine(i, 1))]))
            .IsValid.ShouldBe(valid);
}
