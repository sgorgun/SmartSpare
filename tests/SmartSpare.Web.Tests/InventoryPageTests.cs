using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The parts catalogue on the Inventory page: server paging, search, loading state and errors with a reference.</summary>
public sealed class InventoryPageTests : ClientTestContext
{
    private readonly FakeInventoryClient _inventory = new();

    public InventoryPageTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_first_page_is_requested_from_the_server_and_shown()
    {
        _inventory.Total = 60;

        var page = Render<InventoryPage>();

        page.WaitForAssertion(() => page.FindAll("tbody tr").Count.ShouldBe(FakeInventoryClient.PageLength));
        _inventory.Calls.ShouldHaveSingleItem().ShouldBe((null, 1, PartSearch.DefaultPageSize));
        page.Markup.ShouldContain("P-0001");
        page.Markup.ShouldContain("Engine oil filter");
    }

    [Fact]
    public void While_the_server_answers_the_grid_shows_that_parts_are_loading()
    {
        var pending = new TaskCompletionSource<ApiResult<PagedResponse<PartDto>>>();
        _inventory.Next = pending.Task;

        var page = Render<InventoryPage>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Loading parts…"));
        pending.SetResult(ApiResult.Success(new PagedResponse<PartDto>([FakeInventoryClient.Part(1)], 1, 25, 1)));
        page.WaitForAssertion(() => page.Markup.ShouldNotContain("Loading parts…"));
        page.Markup.ShouldContain("P-0001");
    }

    [Fact]
    public void A_search_reloads_from_the_first_page_with_the_text()
    {
        _inventory.Total = 60;
        var page = Render<InventoryPage>();
        page.WaitForAssertion(() => _inventory.Calls.Count.ShouldBe(1));

        page.Find("[aria-label='Next page']").Click();
        page.WaitForAssertion(() => _inventory.Calls[^1].Page.ShouldBe(2));

        page.Find("input[inputmode=search]").Input("re9");
        page.WaitForAssertion(() => _inventory.Calls[^1].ShouldBe(("re9", 1, PartSearch.DefaultPageSize)), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void An_error_leaves_the_grid_empty_and_shows_the_problem_with_its_reference()
    {
        _inventory.Next = Task.FromResult(ApiResult.Failure<PagedResponse<PartDto>>(
            new ProblemInfo(503, ProblemCodes.ServiceUnavailable, "Service unavailable", null, "4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a")));

        var page = Render<InventoryPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=parts-error]").TextContent
            .ShouldBe("Something went wrong. Please try again. Reference: 4f1c0e9d2b7a4c3e8f6d5b4a3c2e1f0a"));
        page.FindAll("tbody tr.mud-table-row").ShouldBeEmpty();
        page.Markup.ShouldContain("The parts could not be loaded.");
    }

    [Fact]
    public void No_match_says_so()
    {
        _inventory.Total = 0;

        var page = Render<InventoryPage>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("No parts match the search."));
    }
}

/// <summary>An <see cref="InventoryClient"/> that serves a synthetic catalogue of <see cref="Total"/> parts.</summary>
public sealed class FakeInventoryClient() : InventoryClient(new HttpClient())
{
    public const int PageLength = 25;

    public List<(string? Search, int Page, int PageSize)> Calls { get; } = [];

    public int Total { get; set; } = 3;

    /// <summary>The answer to the next call, if the test decides it.</summary>
    public Task<ApiResult<PagedResponse<PartDto>>>? Next { get; set; }

    public static PartDto Part(int number) =>
        new(number, "JD", $"P-{number:D4}", "Engine oil filter", "Engine oil filters", "PCS", 18.40m, 2, 11, "OIL_FILTER", 1, null, false, null, null, false, "1");

    public override Task<ApiResult<PagedResponse<PartDto>>> SearchPartsAsync(
        string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        Calls.Add((search, page, pageSize));
        if (Next is { } next)
        {
            Next = null;
            return next;
        }

        var items = Enumerable.Range((page - 1) * pageSize + 1, Math.Max(0, Math.Min(pageSize, Total - (page - 1) * pageSize)))
            .Select(Part).ToList();
        return Task.FromResult(ApiResult.Success(new PagedResponse<PartDto>(items, page, pageSize, Total)));
    }
}
