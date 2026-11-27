using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Domain;

/// <summary>The adjustment reason catalogue record (STAGE_7_PLAN 3.1, ADR 0025).</summary>
public sealed class AdjustmentReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_reason_is_stored_in_capitals_and_trimmed()
    {
        var reason = AdjustmentReason.Create(" damaged ", "  Damaged in storage ", "out", requiresNote: true).Value;

        (reason.Code, reason.Name, reason.Direction, reason.RequiresNote, reason.IsArchived)
            .ShouldBe(("DAMAGED", "Damaged in storage", AdjustmentReason.Out, true, false));
    }

    [Theory]
    [InlineData("X", "Name", "IN", "Inventory.InvalidReasonCode")]
    [InlineData("WITH SPACE", "Name", "IN", "Inventory.InvalidReasonCode")]
    [InlineData("OK_CODE", " ", "IN", "Catalog.InvalidName")]
    [InlineData("OK_CODE", "Name", "SIDEWAYS", "Inventory.InvalidReasonDirection")]
    public void An_invalid_reason_is_refused(string code, string name, string direction, string error) =>
        AdjustmentReason.Create(code, name, direction, false).Error.Code.ShouldBe(error);

    [Fact]
    public void An_update_reports_whether_anything_changed()
    {
        var reason = AdjustmentReason.Create("LOST", "Lost", "OUT", false).Value;

        reason.Update("Lost", "out", false).Value.ShouldBeFalse();
        reason.Update("Lost or missing", "OUT", false).Value.ShouldBeTrue();
        reason.Update("Lost or missing", "BOTH", true).Value.ShouldBeTrue();
        (reason.Name, reason.Direction, reason.RequiresNote).ShouldBe(("Lost or missing", AdjustmentReason.Both, true));
        reason.Update(new string('n', 201), "BOTH", true).Error.Code.ShouldBe("Catalog.InvalidName");
        reason.Update("Lost", "UP", true).Error.ShouldBe(InventoryErrors.InvalidReasonDirection);
    }

    [Fact]
    public void Archiving_and_restoring_keep_who_and_when()
    {
        var reason = AdjustmentReason.Create("EXPIRED", "Expired", "OUT", false).Value;
        var user = Guid.NewGuid();

        reason.Archive(Now, user);
        reason.Archive(Now.AddDays(1), Guid.NewGuid());
        (reason.IsArchived, reason.ArchivedAt, reason.ArchivedBy).ShouldBe((true, (DateTimeOffset?)Now, (Guid?)user), "archiving twice changes nothing");

        reason.Restore();
        (reason.IsArchived, reason.ArchivedAt, reason.ArchivedBy).ShouldBe((false, (DateTimeOffset?)null, (Guid?)null));
    }

    [Fact]
    public void An_administrator_code_change_replaces_the_code()
    {
        var reason = AdjustmentReason.Create("WET", "Water damage", "OUT", false).Value;

        reason.ChangeCode("DAMP");

        reason.Code.ShouldBe("DAMP");
        Should.Throw<ArgumentException>(() => reason.ChangeCode(" "));
    }
}
