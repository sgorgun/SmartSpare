using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.UnitTests;

/// <summary>The analogue rules of ADR 0027 that the API checks before the database does (SS502).</summary>
public sealed class PartAnalogueTests
{
    private static AnalogueLinkFact Interchangeable(long id, long a, long b) => new(id, Math.Min(a, b), Math.Max(a, b), AnalogueLinkType.Interchangeable);

    private static AnalogueLinkFact Supersedes(long id, long newPart, long oldPart) => new(id, newPart, oldPart, AnalogueLinkType.Supersedes);

    [Fact]
    public void An_interchangeable_link_is_stored_once_with_the_smaller_id_first()
    {
        var link = PartAnalogue.Create(9, 4, AnalogueLinkType.Interchangeable, 1m, new DateOnly(2026, 11, 28), " OEM and aftermarket ", null).Value;

        (link.PartId, link.AnaloguePartId, link.Note).ShouldBe((4L, 9L, "OEM and aftermarket"));
    }

    [Fact]
    public void A_supersession_keeps_its_direction_new_to_old()
    {
        var link = PartAnalogue.Create(9, 4, AnalogueLinkType.Supersedes, 1.2m, new DateOnly(2026, 11, 28), null, null).Value;

        (link.PartId, link.AnaloguePartId).ShouldBe((9L, 4L));
    }

    [Theory]
    [InlineData(0, "Inventory.InvalidScalingFactor")]
    [InlineData(10.5, "Inventory.InvalidScalingFactor")]
    public void A_factor_outside_0_to_10_is_refused(decimal factor, string error) =>
        PartAnalogue.Create(1, 2, AnalogueLinkType.Interchangeable, factor, default, null, null).Error.Code.ShouldBe(error);

    [Fact]
    public void A_part_cannot_be_its_own_analogue() =>
        PartAnalogue.Create(3, 3, AnalogueLinkType.Interchangeable, 1m, default, null, null).Error.Code.ShouldBe("SS502");

    [Fact]
    public void One_link_per_pair_whatever_its_type()
    {
        var existing = new[] { Interchangeable(1, 1, 2) };

        AnalogueRules.Check(Supersedes(0, 2, 1), existing).Error.ShouldBe(AnalogueRules.Duplicate);
        AnalogueRules.Check(Interchangeable(0, 1, 3), existing).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_part_has_at_most_one_successor_but_may_have_several_predecessors()
    {
        var existing = new[] { Supersedes(1, 10, 1) };

        AnalogueRules.Check(Supersedes(0, 11, 1), existing).Error.ShouldBe(AnalogueRules.SecondSuccessor);
        AnalogueRules.Check(Supersedes(0, 10, 2), existing).IsSuccess.ShouldBeTrue("10 replaces both 1 and 2");
    }

    [Fact]
    public void A_supersession_that_closes_a_chain_into_a_cycle_is_refused()
    {
        var existing = new[] { Supersedes(1, 2, 1), Supersedes(2, 3, 2) };   // 3 replaces 2 replaces 1

        AnalogueRules.Check(Supersedes(0, 1, 3), existing).Error.ShouldBe(AnalogueRules.Cycle);
        AnalogueRules.Check(Supersedes(0, 4, 3), existing).IsSuccess.ShouldBeTrue("the chain may grow at its end");
    }

    [Fact]
    public void Interchangeable_links_form_groups_and_never_count_as_a_cycle()
    {
        var existing = new[] { Interchangeable(1, 1, 2), Interchangeable(2, 2, 3) };

        AnalogueRules.Check(Supersedes(0, 1, 3), existing).IsSuccess.ShouldBeTrue("only supersession chains are checked for cycles");
    }

    [Fact]
    public void A_retyped_link_is_checked_without_itself()
    {
        var existing = new[] { Interchangeable(5, 1, 2) };

        AnalogueRules.Check(Supersedes(5, 2, 1), existing).IsSuccess.ShouldBeTrue("the link is not a duplicate of itself");
    }

    [Fact]
    public void Retyping_reports_a_change_of_type_or_direction()
    {
        var link = PartAnalogue.Create(1, 2, AnalogueLinkType.Interchangeable, 1m, default, null, null).Value;

        link.Retype(AnalogueLinkType.Interchangeable, 1).ShouldBeFalse();
        link.Retype(AnalogueLinkType.Supersedes, 2).ShouldBeTrue();
        (link.PartId, link.AnaloguePartId).ShouldBe((2L, 1L), "2 is the new part");
        link.Retype(AnalogueLinkType.Supersedes, 1).ShouldBeTrue("the direction changed");
    }
}
