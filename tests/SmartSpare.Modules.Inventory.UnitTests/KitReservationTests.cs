using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.Modules.Inventory.Features.ServiceKits;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory;

/// <summary>Inventory's answer to a drafted service order (STAGE_6_PLAN 5.3): what is held per line, and whether all of it.</summary>
public sealed class KitReservationTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Expires = new(2026, 12, 4, 9, 0, 0, TimeSpan.Zero);

    private static ServiceOrderDrafted Drafted(params ServiceOrderKitLine[] lines) =>
        new(9001, "SO-2026-000412", 5, lines, Engineer, Expires.AddDays(-7));

    [Fact]
    public void A_fully_held_kit_is_fully_reserved_and_carries_the_actor()
    {
        var reserved = KitReservation.Reserved(
            Drafted(new ServiceOrderKitLine(71, 1001, 1), new ServiceOrderKitLine(72, 1002, 30)),
            [new ReservedLine(71, 1), new ReservedLine(72, 30)], Expires);

        (reserved.ServiceOrderId, reserved.FullyReserved, reserved.ExpiresAt, reserved.ActorId).ShouldBe((9001L, true, Expires, Engineer));
        reserved.Lines.Select(l => (l.LineId, l.ReservedQty)).ShouldBe([(71L, 1m), (72L, 30m)]);
    }

    [Fact]
    public void A_short_line_makes_the_kit_partial_and_a_missing_answer_counts_as_nothing_held()
    {
        var reserved = KitReservation.Reserved(
            Drafted(new ServiceOrderKitLine(71, 1001, 1), new ServiceOrderKitLine(72, 1002, 30), new ServiceOrderKitLine(73, 1003, 2)),
            [new ReservedLine(71, 1), new ReservedLine(72, 12)], Expires);

        reserved.FullyReserved.ShouldBeFalse();
        reserved.Lines.Select(l => (l.LineId, l.ReservedQty)).ShouldBe([(71L, 1m), (72L, 12m), (73L, 0m)]);
    }

    [Fact]
    public void A_kit_without_lines_is_not_fully_reserved()
    {
        KitReservation.Reserved(Drafted(), [], Expires).FullyReserved.ShouldBeFalse();
    }
}
