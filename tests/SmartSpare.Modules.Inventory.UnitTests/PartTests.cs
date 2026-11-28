using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.UnitTests;

/// <summary>The part as a catalogue record (STAGE_7_PLAN 3.1, PR 7i1): OEM number as code, description as name.</summary>
public sealed class PartTests
{
    [Fact]
    public void A_part_is_created_with_a_normalised_oem_number_and_description()
    {
        var part = Part.Create(2, " re 504836 ", "  Engine   oil filter ", 11, 1, 0.45m, 18.40m, false, 3).Value;

        (part.Code, part.Name, part.BrandId, part.ManufacturerId).ShouldBe(("RE504836", "Engine oil filter", (short)2, (short?)3));
    }

    [Theory]
    [InlineData("AB", "Engine oil filter", "Inventory.InvalidOemNumber")]
    [InlineData("-AB1", "Engine oil filter", "Inventory.InvalidOemNumber")]
    [InlineData("RE504836", "ab", "Inventory.InvalidPartDescription")]
    public void Invalid_numbers_and_descriptions_are_refused(string oem, string description, string error) =>
        Part.Create(2, oem, description, 11, 1, null, 1m, false, null).Error.Code.ShouldBe(error);

    [Fact]
    public void A_description_of_300_characters_is_kept_and_a_longer_one_refused()
    {
        Part.Create(2, "RE504836", new string('a', Part.MaxDescriptionLength), 11, 1, null, 1m, false, null).IsSuccess.ShouldBeTrue();
        Part.Create(2, "RE504836", new string('a', Part.MaxDescriptionLength + 1), 11, 1, null, 1m, false, null)
            .Error.Code.ShouldBe("Inventory.InvalidPartDescription");
    }

    [Fact]
    public void Negative_cost_or_weight_is_refused() =>
        (Part.Create(2, "RE504836", "Filter", 11, 1, null, -1m, false, null).Error.Code,
         Part.Create(2, "RE504836", "Filter", 11, 1, -0.1m, 1m, false, null).Error.Code)
            .ShouldBe(("Inventory.InvalidUnitCost", "Inventory.InvalidWeight"));

    [Fact]
    public void An_update_reports_a_change_and_may_clear_the_manufacturer()
    {
        var part = Part.Create(2, "RE504836", "Engine oil filter", 11, 1, 0.45m, 18.40m, false, 3).Value;

        part.Update("Engine oil filter", 11, 1, 0.45m, 18.40m, false, 3).Value.ShouldBeFalse("nothing changed");
        part.Update("Engine oil filter", 11, 1, 0.45m, 18.40m, false, null).Value.ShouldBeTrue();
        part.ManufacturerId.ShouldBeNull("the part editor clears a manufacturer; the import never does");
    }
}
