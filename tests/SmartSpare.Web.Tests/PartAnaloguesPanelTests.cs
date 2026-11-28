using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The analogues panel of the part editor (ADR 0027, PR 7i2b): links seen from the part, group and chain, linking, and a
/// removal that needs the user's reason in the confirmation.
/// </summary>
public sealed class PartAnaloguesPanelTests : ClientTestContext
{
    private readonly FakeAnalogueClient _inventory = new();

    public PartAnaloguesPanelTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddScoped<CatalogClient>(_ => new FakePartsCatalogClient());
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_links_group_and_chain_are_shown_from_the_part()
    {
        var panel = Render<PartAnaloguesPanel>(p => p.Add(x => x.PartId, 1042L).Add(x => x.PartName, "RE504836").Add(x => x.MayEdit, true));

        panel.WaitForAssertion(() => panel.FindAll("[data-testid=analogue-link]").Count.ShouldBe(2));
        panel.Find("[data-testid=analogues-links]").TextContent.ShouldContain("Interchangeable");
        panel.Find("[data-testid=analogues-links]").TextContent.ShouldContain("Replaced by");
        panel.Find("[data-testid=analogues-chain]").TextContent.ShouldContain("JD RE504836 → JD RE504900");
        panel.Find("[data-testid=analogues-group]").TextContent.ShouldContain("MANN W1170-12");
    }

    [Fact]
    public void A_removal_needs_the_users_reason_and_sends_it_with_the_version()
    {
        var panel = Render<PartAnaloguesPanel>(p => p.Add(x => x.PartId, 1042L).Add(x => x.PartName, "RE504836").Add(x => x.MayEdit, true));
        panel.WaitForAssertion(() => panel.FindAll("[data-testid=analogue-remove]").Count.ShouldBe(2));

        panel.FindAll("[data-testid=analogue-remove]")[0].Click();
        panel.Find("[data-testid=analogue-remove-yes]").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid=analogues-error]").TextContent.ShouldContain("Give a reason"));
        _inventory.Removed.ShouldBeEmpty("no request without the user's reason");

        panel.Find("input[data-testid=analogue-remove-reason], [data-testid=analogue-remove-reason] input").Change("Pressure test failed");
        panel.Find("[data-testid=analogue-remove-yes]").Click();
        panel.WaitForAssertion(() => _inventory.Removed.ShouldHaveSingleItem().ShouldBe((7L, "Pressure test failed", "41")));
    }

    [Fact]
    public void Without_editing_the_panel_is_read_only()
    {
        var panel = Render<PartAnaloguesPanel>(p => p.Add(x => x.PartId, 1042L).Add(x => x.PartName, "RE504836").Add(x => x.MayEdit, false));

        panel.WaitForAssertion(() => panel.FindAll("[data-testid=analogue-link]").Count.ShouldBe(2));
        panel.FindAll("[data-testid=analogue-remove]").ShouldBeEmpty();
        panel.FindAll("[data-testid=analogue-add]").ShouldBeEmpty();
    }
}

/// <summary>An <see cref="InventoryClient"/> with one part's analogues.</summary>
public sealed class FakeAnalogueClient() : InventoryClient(new HttpClient())
{
    private static readonly PartRef Self = new(1042, "JD", "RE504836", "Engine oil filter", false);
    private static readonly PartRef Aftermarket = new(2210, "MANN", "W1170-12", "Oil filter, aftermarket", false);
    private static readonly PartRef Successor = new(1050, "JD", "RE504900", "Engine oil filter, new design", false);

    public List<(long Id, string Reason, string Version)> Removed { get; } = [];

    public override Task<ApiResult<PartAnaloguesView>> GetPartAnaloguesAsync(long partId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PartAnaloguesView(
            Self,
            [
                new PartAnalogueLink(7, AnalogueRelations.Interchangeable, Aftermarket, 1m, new DateOnly(2026, 11, 1), null, "41"),
                new PartAnalogueLink(8, AnalogueRelations.ReplacedBy, Successor, 1m, new DateOnly(2026, 11, 1), null, "42"),
            ],
            [Self, Aftermarket],
            [Self, Successor])));

    public override Task<ApiResult<NoContent>> RemovePartAnalogueAsync(long id, string reason, string version, CancellationToken cancellationToken = default)
    {
        Removed.Add((id, reason, version));
        return Task.FromResult(ApiResult.Success(new NoContent()));
    }
}
