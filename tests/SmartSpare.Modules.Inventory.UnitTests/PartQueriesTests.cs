using FluentValidation.TestHelper;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Features.GetPart;
using SmartSpare.SharedKernel;

using SmartSpare.Modules.Inventory.Features;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The rules of the parts queries that need no database: literal search text and the not-found error (paging limits of
/// the catalogue list are the shared mechanism's, PR 7i1). The SQL itself is covered by <c>InventoryApiTests</c> on PostgreSQL.
/// </summary>
public sealed class PartQueriesTests
{
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
