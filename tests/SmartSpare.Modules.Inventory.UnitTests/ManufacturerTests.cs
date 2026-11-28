using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.UnitTests;

/// <summary>The manufacturer as Inventory's master record (STAGE_7_PLAN 3.1, PR 7h3).</summary>
public sealed class ManufacturerTests
{
    [Fact]
    public void A_manufacturer_is_created_with_a_normalised_code_and_optional_country_and_website()
    {
        var full = Manufacturer.Create(" mann ", "MANN+HUMMEL", "de", " https://www.mann-hummel.com ").Value;
        (full.Code, full.CountryCode, full.Website).ShouldBe(("MANN", "DE", "https://www.mann-hummel.com"));

        var bare = Manufacturer.Create("BOSCH", "Bosch", " ", null).Value;
        (bare.CountryCode, bare.Website).ShouldBe(((string?)null, (string?)null));
    }

    [Theory]
    [InlineData("M", "DE", null, "Inventory.InvalidManufacturerCode")]
    [InlineData("MANN", "DEU", null, "Inventory.InvalidManufacturerCountry")]
    [InlineData("MANN", "DE", "ftp://mann.example", "Inventory.InvalidManufacturerWebsite")]
    [InlineData("MANN", "DE", "not a url", "Inventory.InvalidManufacturerWebsite")]
    public void Invalid_values_are_refused(string code, string country, string? website, string error) =>
        Manufacturer.Create(code, "Name", country, website).Error.Code.ShouldBe(error);

    [Fact]
    public void An_update_reports_whether_anything_changed_and_a_refused_one_changes_nothing()
    {
        var maker = Manufacturer.Create("MANN", "MANN+HUMMEL", "DE", null).Value;

        maker.Update("MANN+HUMMEL", "de", "").Value.ShouldBeFalse();
        maker.Update("MANN+HUMMEL", "DE", "https://mann.example").Value.ShouldBeTrue();
        maker.Update("Other", "XXX", null).Error.Code.ShouldBe("Inventory.InvalidManufacturerCountry");
        (maker.Name, maker.Website).ShouldBe(("MANN+HUMMEL", "https://mann.example"));
    }
}
