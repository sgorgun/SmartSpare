using FluentValidation.TestHelper;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Features.GetPart;
using SmartSpare.Modules.Inventory.Features.SearchParts;
using SmartSpare.SharedKernel;

using SmartSpare.Modules.Inventory.Features;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The rules of the parts queries that need no database: paging limits, literal search text and the not-found error.
/// The SQL itself is covered by <c>InventoryApiTests</c> on PostgreSQL.
/// </summary>
public sealed class PartQueriesTests
{
    private readonly SearchPartsQueryValidator _validator = new();

    [Theory]
    [InlineData(null, 1, PartSearch.DefaultPageSize)]
    [InlineData("", 1, 1)]
    [InlineData("cp-4821", 9, PartSearch.MaxPageSize)]
    public void Valid_searches_pass(string? search, int page, int pageSize) =>
        _validator.TestValidate(new SearchPartsQuery(search, page, pageSize)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(0, 25, nameof(SearchPartsQuery.Page))]
    [InlineData(-1, 25, nameof(SearchPartsQuery.Page))]
    [InlineData(1, 0, nameof(SearchPartsQuery.PageSize))]
    [InlineData(1, PartSearch.MaxPageSize + 1, nameof(SearchPartsQuery.PageSize))]
    public void Paging_outside_the_limits_fails_on_its_field(int page, int pageSize, string field) =>
        _validator.TestValidate(new SearchPartsQuery(null, page, pageSize)).ShouldHaveValidationErrorFor(field);

    [Fact]
    public void A_search_longer_than_any_description_fails() =>
        _validator.TestValidate(new SearchPartsQuery(new string('x', PartSearch.MaxSearchLength + 1), 1, 25))
            .ShouldHaveValidationErrorFor(q => q.Search);

    [Theory]
    [InlineData("CP-4821", "CP-4821")]
    [InlineData("100%", @"100\%")]
    [InlineData("A_B", @"A\_B")]
    [InlineData(@"C:\x", @"C:\\x")]
    [InlineData(@"\%_", @"\\\%\_")]
    public void Like_wildcards_in_the_search_text_are_escaped(string text, string escaped) =>
        SearchText.Escape(text).ShouldBe(escaped);

    [Fact]
    public void An_unknown_part_is_a_not_found_error_with_a_stable_code()
    {
        var error = PartErrors.NotFound(42);

        error.Type.ShouldBe(ErrorType.NotFound);
        error.Code.ShouldBe("Inventory.PartNotFound");
        error.Description.ShouldBe("Part 42 does not exist.");
    }
}
