using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.UnitTests;

/// <summary>The brand as Inventory's master record (STAGE_7_PLAN 3.2, PR 7h1).</summary>
public sealed class BrandTests
{
    [Fact]
    public void A_brand_is_created_with_a_normalised_code_and_country()
    {
        var brand = Brand.Create(" claas ", "CLAAS", "de").Value;

        (brand.Code, brand.Name, brand.CountryCode, brand.IsArchived).ShouldBe(("CLAAS", "CLAAS", "DE", false));
    }

    [Theory]
    [InlineData("C", "Inventory.InvalidBrandCode")]
    [InlineData("TOO_LONG_CODE", "Inventory.InvalidBrandCode")]
    [InlineData("A-B", "Inventory.InvalidBrandCode")]
    public void A_code_needs_2_to_10_capital_letters_digits_or_underscores(string code, string error) =>
        Brand.Create(code, "Name", "DE").Error.Code.ShouldBe(error);

    [Theory]
    [InlineData("D")]
    [InlineData("DEU")]
    [InlineData("1A")]
    public void The_country_is_two_letters(string country) =>
        Brand.Create("JD", "John Deere", country).Error.Code.ShouldBe("Inventory.InvalidBrandCountry");

    [Fact]
    public void A_name_fits_the_80_characters_of_the_views()
    {
        Brand.Create("JD", new string('x', 81), "US").Error.Code.ShouldBe("Inventory.BrandNameTooLong");
        Brand.Create("JD", "", "US").Error.Code.ShouldBe("Catalog.InvalidName");
        Brand.Create("JD", new string('x', 80), "US").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_update_reports_whether_anything_changed()
    {
        var brand = Brand.Create("JD", "John Deere", "US").Value;

        brand.Update("John Deere", "us").Value.ShouldBeFalse();
        brand.Update("John Deere GmbH", "DE").Value.ShouldBeTrue();
        (brand.Name, brand.CountryCode).ShouldBe(("John Deere GmbH", "DE"));
        brand.Update("x", "DEU").Error.Code.ShouldBe("Inventory.InvalidBrandCountry");
        brand.Update(new string('x', 81), "DE").Error.Code.ShouldBe("Inventory.BrandNameTooLong");
        brand.Name.ShouldBe("John Deere GmbH", "a refused update changes nothing");
    }
}
