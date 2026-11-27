using System.Reflection;
using System.Text.Json;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Features.ServiceOrders;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// The service-kit flow on Fleet's side (STAGE_6_PLAN 5.3): the drafted order as <see cref="ServiceOrderDrafted"/>, and
/// Inventory's answer applied to the order (qty_reserved copy, parts reserved, late answers ignored).
/// </summary>
public sealed class KitReservationTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A draft with database ids, as EF loads it: lines 71 (part 1002, 30) and 72 (part 1001, 1).</summary>
    private static ServiceOrder Draft()
    {
        var order = ServiceOrder.DraftFromPlan(501, 21, 5, 500, 512, Engineer, [new(1002, 30), new(1001, 1)]).Value;
        SetId(order, 9001);
        SetId(order.Lines[0], 71);
        SetId(order.Lines[1], 72);
        typeof(ServiceOrder).GetProperty(nameof(ServiceOrder.Number))!.SetValue(order, "SO-2026-000412");
        return order;
    }

    private static void SetId<TId>(Entity<TId> entity, TId id)
        where TId : notnull =>
        typeof(Entity<TId>).GetProperty(nameof(Entity<TId>.Id), BindingFlags.Public | BindingFlags.Instance)!.SetValue(entity, id);

    [Fact]
    public void A_drafted_order_becomes_ServiceOrderDrafted_with_its_kit_in_part_order()
    {
        var drafted = ServiceOrderEvents.Drafted(Draft(), Engineer, Now);

        (drafted.ServiceOrderId, drafted.Number, drafted.WarehouseId, drafted.ActorId, drafted.DraftedAt)
            .ShouldBe((9001L, "SO-2026-000412", (short)5, Engineer, Now));
        drafted.Lines.ShouldBe([new ServiceOrderKitLine(72, 1001, 1), new ServiceOrderKitLine(71, 1002, 30)]);
    }

    [Fact]
    public void ServiceOrderDrafted_round_trips_through_the_outbox_with_a_stable_shape()
    {
        var sent = ServiceOrderEvents.Drafted(Draft(), Engineer, Now) with { OccurredAt = Now };

        var message = IntegrationEventSerializer.ToOutboxMessage(sent);
        var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBeOfType<ServiceOrderDrafted>();

        message.Type.ShouldBe("SmartSpare.Modules.Fleet.Contracts.ServiceOrderDrafted, SmartSpare.Modules.Fleet.Contracts");
        (received with { Lines = sent.Lines }).ShouldBe(sent);
        received.Lines.ShouldBe(sent.Lines);
        JsonDocument.Parse(message.Payload).RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["id", "occurredAt", "serviceOrderId", "number", "warehouseId", "lines", "actorId", "draftedAt"], ignoreOrder: true);
    }

    [Fact]
    public void A_fully_held_kit_moves_the_draft_to_parts_reserved()
    {
        var order = Draft();

        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 30, [72] = 1 }, Now).ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.PartsReserved);
        order.Lines.Select(l => (l.QtyReserved, l.QtyMissing)).ShouldBe([(30m, 0m), (1m, 0m)]);
    }

    [Fact]
    public void A_partial_kit_stays_a_draft_and_shows_what_is_missing()
    {
        var order = Draft();

        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 12, [72] = 1 }, Now).ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.Draft);
        order.Lines[0].QtyReserved.ShouldBe(12);
        order.Lines[0].QtyMissing.ShouldBe(18);
    }

    [Fact]
    public void A_held_quantity_never_exceeds_what_is_still_to_be_issued()
    {
        var order = Draft();

        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 99, [72] = -1 }, Now);

        order.Lines.Select(l => l.QtyReserved).ShouldBe([30m, 0m]);
    }

    [Fact]
    public void A_reservation_for_a_started_order_updates_the_copy_but_not_the_status()
    {
        var order = Draft();
        order.Start().IsSuccess.ShouldBeTrue();

        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 30, [72] = 1 }, Now).ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.InProgress);
        order.Lines[0].QtyReserved.ShouldBe(30);
    }

    [Fact]
    public void A_late_reservation_for_a_closed_order_is_ignored()
    {
        var order = Draft();
        order.Cancel("Customer sold the machine", Now).IsSuccess.ShouldBeTrue();

        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 30 }, Now).ShouldBeFalse();

        order.Status.ShouldBe(ServiceOrderStatus.Cancelled);
        order.Lines[0].QtyReserved.ShouldBe(0);
    }

    [Fact]
    public void Expired_holds_clear_the_copy_and_reopen_a_parts_reserved_order()
    {
        var order = Draft();
        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 30, [72] = 1 }, Now);

        order.ApplyKitReservationExpired(new HashSet<long> { 71 }).ShouldBeTrue();

        order.Status.ShouldBe(ServiceOrderStatus.Draft);
        order.Lines.Select(l => l.QtyReserved).ShouldBe([0m, 1m]);
    }

    [Fact]
    public void Expiry_of_other_lines_changes_nothing_and_a_closed_order_is_left_alone()
    {
        var order = Draft();
        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 30, [72] = 1 }, Now);
        order.ApplyKitReservationExpired(new HashSet<long> { 999 }).ShouldBeTrue();
        order.Status.ShouldBe(ServiceOrderStatus.PartsReserved);

        order.Complete(515, 512, Now).IsSuccess.ShouldBeTrue();
        order.ApplyKitReservationExpired(new HashSet<long> { 71, 72 }).ShouldBeFalse();
        order.Lines.Select(l => l.QtyReserved).ShouldBe([30m, 1m]);
    }
}
