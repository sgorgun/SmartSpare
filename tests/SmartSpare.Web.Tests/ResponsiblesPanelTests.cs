using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Features.MasterData;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The responsible employees of a record (ADR 0026, STAGE_7_PLAN 3.7): the panel lists them with the roles that give
/// rights marked, removing and saving sends the complete new set, and a refusal shows the server's sentence.
/// </summary>
public sealed class ResponsiblesPanelTests : ClientTestContext
{
    private static readonly Guid Egle = Guid.Parse("00000000-0000-4000-8000-000000000006");
    private static readonly Guid Laura = Guid.Parse("00000000-0000-4000-8000-000000000104");

    private readonly FakeResponsiblesClient _inventory = new();

    public ResponsiblesPanelTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddScoped<IdentityClient>(_ => new FakeOrganisationClient());
        Services.AddLogging();
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_panel_lists_the_responsibles_and_marks_the_roles_that_give_rights()
    {
        var panel = RenderPanel();

        panel.WaitForAssertion(() => panel.Find("[data-testid=responsibles-list]").TextContent.ShouldContain("Eglė Stankevičiūtė"));
        panel.Find("[data-testid=responsibles-list]").TextContent.ShouldContain("Gives rights");
        panel.Find("[data-testid=responsibles-list]").TextContent.ShouldContain("Laura Sabaliauskienė");
        panel.Find("section").TextContent.ShouldContain("within 30 seconds");
    }

    [Fact]
    public void Removing_and_saving_sends_the_complete_new_set()
    {
        var panel = RenderPanel();
        panel.WaitForAssertion(() => panel.FindAll("[data-testid=remove-responsible]").Count.ShouldBe(2));

        panel.FindAll("[data-testid=remove-responsible]")[1].Click();
        panel.Find("[data-testid=save-responsibles]").Click();

        panel.WaitForAssertion(() => _inventory.Saved.ShouldHaveSingleItem());
        var (segment, id, request) = _inventory.Saved[0];
        (segment, id).ShouldBe(("warehouses", (short)3));
        request.Responsibles.ShouldBe([new ResponsibleEntry(Egle, "KEEPER")]);
    }

    [Fact]
    public void A_refused_change_shows_the_servers_sentence()
    {
        _inventory.NextSave = ApiResult.Failure<IReadOnlyList<ResponsibleDto>>(new ProblemInfo(422, "Inventory.UnknownEmployee", "Business rule violated",
            "An employee does not exist or has left the company.", "ref-emp"));
        var panel = RenderPanel();
        panel.WaitForAssertion(() => panel.FindAll("[data-testid=remove-responsible]").Count.ShouldBe(2));

        panel.FindAll("[data-testid=remove-responsible]")[0].Click();
        panel.Find("[data-testid=save-responsibles]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=responsibles-error]").TextContent
            .ShouldBe("An employee does not exist or has left the company. Reference: ref-emp"));
    }

    private IRenderedComponent<ResponsiblesPanel> RenderPanel() =>
        Render<ResponsiblesPanel>(p => p
            .Add(x => x.Segment, "warehouses")
            .Add(x => x.Kind, "warehouse")
            .Add(x => x.RecordId, (short)3)
            .Add(x => x.RecordName, "KLP Klaipėda"));

    private sealed class FakeResponsiblesClient() : InventoryClient(new HttpClient())
    {
        public List<(string Segment, short Id, ResponsiblesRequest Request)> Saved { get; } = [];

        public ApiResult<IReadOnlyList<ResponsibleDto>>? NextSave { get; set; }

        public override Task<ApiResult<IReadOnlyList<AssignmentRoleDto>>> ListAssignmentRolesAsync(string kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Success<IReadOnlyList<AssignmentRoleDto>>(
                [new AssignmentRoleDto("warehouse", "KEEPER", "Keeper", true), new AssignmentRoleDto("warehouse", "HUB_MANAGER", "Hub manager", false)]));

        public override Task<ApiResult<IReadOnlyList<ResponsibleDto>>> GetResponsiblesAsync(string segment, short id, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult.Success<IReadOnlyList<ResponsibleDto>>(
            [
                new ResponsibleDto(Egle, "E0006", "Eglė Stankevičiūtė", true, "KEEPER", "Keeper", true),
                new ResponsibleDto(Laura, "E0104", "Laura Sabaliauskienė", true, "HUB_MANAGER", "Hub manager", false),
            ]));

        public override Task<ApiResult<IReadOnlyList<ResponsibleDto>>> ReplaceResponsiblesAsync(
            string segment, short id, ResponsiblesRequest request, CancellationToken cancellationToken = default)
        {
            Saved.Add((segment, id, request));
            return Task.FromResult(NextSave ?? ApiResult.Success<IReadOnlyList<ResponsibleDto>>([]));
        }
    }
}
