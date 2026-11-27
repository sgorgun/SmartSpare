using System.Reflection;
using System.Text.Json;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Features.ServiceOrders;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet;

/// <summary>
/// Completion and kit issue on Fleet's side (STAGE_6_PLAN 5.4, PR 6h2): the state machine's conflicts, the idempotent
/// repeat, the open quantities sent to Inventory and the issued quantities applied (shortfall rule F7).
/// </summary>
public sealed class CompletionTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A draft with database ids: lines 71 (part 1002, 30) and 72 (part 1001, 1).</summary>
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
    public void Complete_twice_cancel_after_complete_and_start_after_cancel_are_conflicts()
    {
        var completed = Draft();
        completed.Complete(515, 512, Now).IsSuccess.ShouldBeTrue();
        completed.Complete(520, 512, Now).Error.Code.ShouldBe("SS300");
        completed.Cancel("Too late", Now).Error.Code.ShouldBe("SS300");

        var cancelled = Draft();
        cancelled.Cancel("Machine sold", Now).IsSuccess.ShouldBeTrue();
        cancelled.Start().Error.Code.ShouldBe("SS300");
        cancelled.Complete(515, 512, Now).Error.ShouldBe(FleetErrors.InvalidTransition(ServiceOrderStatus.Cancelled, "completed"));
    }

    [Fact]
    public void Only_the_same_hours_on_a_completed_order_count_as_a_repeat()
    {
        var order = Draft();
        order.IsSameCompletion(515).ShouldBeFalse("not completed yet");

        order.Complete(515, 512, Now);

        order.IsSameCompletion(515).ShouldBeTrue();
        order.IsSameCompletion(516).ShouldBeFalse();
    }

    [Fact]
    public void The_completion_event_carries_what_is_still_to_be_issued_in_part_order()
    {
        var order = Draft();
        order.Complete(515, 512, Now);

        var completed = ServiceOrderEvents.Completed(order, Engineer, Now);

        (completed.ServiceOrderId, completed.Number, completed.WarehouseId, completed.ActorId, completed.CompletedAt)
            .ShouldBe((9001L, "SO-2026-000412", (short)5, Engineer, Now));
        completed.Lines.ShouldBe([new ServiceOrderKitLine(72, 1001, 1), new ServiceOrderKitLine(71, 1002, 30)]);
        completed.ShouldBeAssignableTo<IActedByUser>();
    }

    [Fact]
    public void ServiceOrderCompleted_round_trips_through_the_outbox()
    {
        var order = Draft();
        order.Complete(515, 512, Now);
        var sent = ServiceOrderEvents.Completed(order, Engineer, Now) with { OccurredAt = Now };

        var message = IntegrationEventSerializer.ToOutboxMessage(sent);
        var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBeOfType<ServiceOrderCompleted>();

        (received with { Lines = sent.Lines }).ShouldBe(sent);
        received.Lines.ShouldBe(sent.Lines);
        JsonDocument.Parse(message.Payload).RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["id", "occurredAt", "serviceOrderId", "number", "warehouseId", "lines", "actorId", "completedAt"], ignoreOrder: true);
    }

    [Fact]
    public void The_issued_quantities_are_stored_and_the_rest_is_the_shortfall()
    {
        var order = Draft();
        order.ApplyKitReserved(new Dictionary<long, decimal> { [71] = 24, [72] = 1 }, Now);
        order.Complete(515, 512, Now);

        order.ApplyKitIssued(new Dictionary<long, decimal> { [71] = 24, [72] = 1 }).ShouldBeTrue();

        order.Lines.Select(l => (l.QtyIssued, l.QtyReserved, l.QtyMissing)).ShouldBe([(24m, 0m, 6m), (1m, 0m, 0m)]);
        order.Kit.ShouldBe(KitStatus.Issued);
    }

    [Fact]
    public void An_issue_never_exceeds_the_requirement_and_is_ignored_for_an_open_order()
    {
        var open = Draft();
        open.ApplyKitIssued(new Dictionary<long, decimal> { [71] = 30 }).ShouldBeFalse();
        open.Lines[0].QtyIssued.ShouldBe(0);

        var completed = Draft();
        completed.Complete(515, 512, Now);
        completed.ApplyKitIssued(new Dictionary<long, decimal> { [71] = 99 });
        completed.Lines[0].QtyIssued.ShouldBe(30);
    }

    [Fact]
    public void Complete_is_offered_with_start_to_engineers_and_not_once_completed()
    {
        var access = new ServiceOrderAccess(new Roles(AppRoles.ServiceEngineer));
        var order = Draft();

        access.AllowedActions(order).ShouldBe([ServiceOrderActions.Start, ServiceOrderActions.Complete, ServiceOrderActions.Cancel]);
        order.Start();
        access.AllowedActions(order).ShouldBe([ServiceOrderActions.Complete, ServiceOrderActions.Cancel]);
        order.Complete(515, 512, Now);
        access.AllowedActions(order).ShouldBeEmpty();
        new ServiceOrderAccess(new Roles(AppRoles.BrandManager)).AllowedActions(Draft()).ShouldNotContain(ServiceOrderActions.Complete);
    }

    [Theory]
    [InlineData(515, true)]
    [InlineData(515.5, true)]
    [InlineData(515.55, false)]
    [InlineData(-1, false)]
    [InlineData(100000.1, false)]
    public void The_completion_hours_are_valid_engine_hours(double hours, bool valid)
    {
        new CompleteServiceOrderRequestValidator().Validate(new CompleteServiceOrderRequest((decimal)hours)).IsValid.ShouldBe(valid);
        new CompleteServiceOrderCommandValidator().Validate(new CompleteServiceOrderCommand(1, (decimal)hours)).IsValid.ShouldBe(valid);
    }

    private sealed class Roles(params string[] roles) : ICurrentUser
    {
        public Guid? UserId => Engineer;

        public string? Reason => null;

        IReadOnlyCollection<string> ICurrentUser.Roles => roles;

        public short? HomeWarehouseId => null;
    }
}
