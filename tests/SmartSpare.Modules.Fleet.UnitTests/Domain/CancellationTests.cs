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

/// <summary>Cancellation on Fleet's side (STAGE_6_PLAN 5.5, PR 6h3): the event to Inventory, the release request, the rules.</summary>
public sealed class CancellationTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

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
    public void The_cancellation_event_names_every_line_and_the_reason()
    {
        var order = Draft();
        order.Cancel("Customer sold the machine", Now).IsSuccess.ShouldBeTrue();

        var cancelled = ServiceOrderEvents.Cancelled(order, order.CancelReason!, Engineer, Now);

        (cancelled.ServiceOrderId, cancelled.Number, cancelled.Reason, cancelled.ActorId, cancelled.CancelledAt)
            .ShouldBe((9001L, "SO-2026-000412", "Customer sold the machine", Engineer, Now));
        cancelled.LineIds.ShouldBe([71L, 72L]);
    }

    [Fact]
    public void ServiceOrderCancelled_and_ServiceKitReleaseRequested_round_trip_through_the_outbox()
    {
        var order = Draft();
        order.Cancel("Duplicate", Now);
        var cancelled = ServiceOrderEvents.Cancelled(order, "Duplicate", Engineer, Now) with { OccurredAt = Now };
        var release = new ServiceKitReleaseRequested(9001, [71, 72], Engineer) { OccurredAt = Now };

        foreach (var (sent, names) in new (IIntegrationEvent, string[])[]
                 {
                     (cancelled, ["id", "occurredAt", "serviceOrderId", "number", "lineIds", "reason", "actorId", "cancelledAt"]),
                     (release, ["id", "occurredAt", "serviceOrderId", "lineIds", "actorId"]),
                 })
        {
            var message = IntegrationEventSerializer.ToOutboxMessage(sent);
            var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload);
            received.GetType().ShouldBe(sent.GetType());
            received.ShouldBeAssignableTo<IActedByUser>()!.ActorId.ShouldBe(Engineer);
            JsonDocument.Parse(message.Payload).RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(names, ignoreOrder: true);
        }
    }

    [Fact]
    public void Only_an_open_order_with_a_reason_is_cancelled()
    {
        Draft().Cancel(" ", Now).Error.ShouldBe(FleetErrors.CancelReasonRequired);

        var completed = Draft();
        completed.Complete(515, 512, Now);
        completed.Cancel("Too late", Now).Error.Code.ShouldBe("SS300");

        var cancelled = Draft();
        cancelled.Cancel("Duplicate", Now);
        cancelled.Cancel("Again", Now).Error.Code.ShouldBe("SS300");
        cancelled.Kit.ShouldBe(KitStatus.Released);
    }

    [Theory]
    [InlineData("Customer sold the machine", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void A_cancellation_reason_is_required(string reason, bool valid)
    {
        new CancelServiceOrderRequestValidator().Validate(new CancelServiceOrderRequest(reason)).IsValid.ShouldBe(valid);
        new CancelServiceOrderCommandValidator().Validate(new CancelServiceOrderCommand(1, reason)).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void A_reason_has_at_most_300_characters()
    {
        new CancelServiceOrderRequestValidator().Validate(new CancelServiceOrderRequest(new string('x', 301))).IsValid.ShouldBeFalse();
        new CancelServiceOrderRequestValidator().Validate(new CancelServiceOrderRequest(new string('x', 300))).IsValid.ShouldBeTrue();
    }
}
