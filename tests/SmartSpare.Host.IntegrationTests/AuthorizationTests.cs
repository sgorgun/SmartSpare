using System.Net;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;
using static SmartSpare.Host.IntegrationTests.Support.SecurityTestHost;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Policies and the brand scope through HTTP (STAGE_3_PLAN 3.2, 3.3 and 9.2), on test endpoints that are written the way
/// stage 5–8 approval endpoints will be: policy first, then the brand-scope requirement against the document's lines.
/// </summary>
public sealed class AuthorizationTests(SecurityTestHost host) : IClassFixture<SecurityTestHost>
{
    // The brand manager of the scenario in section 9.2 is responsible for Claas and Lemken.
    private static readonly TestUser ClaasLemkenManager = TestUser.Create("bm.claas.lemken", [AppRoles.BrandManager], [Claas, Lemken]);
    private static readonly TestUser ManagerAndSponsor = TestUser.Create("bm.sponsor", [AppRoles.BrandManager, AppRoles.Sponsor], [Claas]);
    private static readonly TestUser Planner = TestUser.Create("planner", [AppRoles.InventoryPlanner]);
    private static readonly TestUser Administrator = TestUser.Create("admin", [AppRoles.Administrator]);
    private static readonly TestUser Keeper = TestUser.Create("keeper", [AppRoles.WarehouseKeeper], warehouse: 1);

    [Fact]
    public async Task A_brand_manager_approves_a_document_with_only_own_brands()
    {
        var transfer = host.AddDocument(Claas, Claas, Lemken);

        (await ActAsync(ClaasLemkenManager, $"/api/test/transfers/{transfer}/approve")).ShouldBe(HttpStatusCode.OK);

        (await StateAsync(transfer)).ShouldBe("approved");
    }

    [Fact]
    public async Task One_foreign_brand_line_forbids_the_approval_and_leaves_the_document_unchanged()
    {
        var transfer = host.AddDocument(Claas, JohnDeere);

        (await ActAsync(ClaasLemkenManager, $"/api/test/transfers/{transfer}/approve")).ShouldBe(HttpStatusCode.Forbidden);

        (await StateAsync(transfer)).ShouldBe("draft");
    }

    [Fact]
    public async Task Reads_are_not_brand_scoped()
    {
        var foreign = host.AddDocument(JohnDeere);
        using var browser = host.NewBrowser();

        using var response = await browser.GetAsync($"/api/test/documents/{foreign}", bearer: host.Token(ClaasLemkenManager));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unscoped_role_overrides_the_brand_scope_for_its_own_actions_only()
    {
        // As SPONSOR the user may approve any purchase order ...
        var purchaseOrder = host.AddDocument(JohnDeere);
        (await ActAsync(ManagerAndSponsor, $"/api/test/purchase-orders/{purchaseOrder}/approve")).ShouldBe(HttpStatusCode.OK);

        // ... but SPONSOR grants nothing for transfers, so the brand scope of BRAND_MANAGER still applies there.
        var transfer = host.AddDocument(JohnDeere);
        (await ActAsync(ManagerAndSponsor, $"/api/test/transfers/{transfer}/approve")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/test/transfers/{0}/cancel")]
    [InlineData("/api/test/purchase-orders/{0}/cancel")]
    public async Task A_brand_manager_cannot_cancel_a_mixed_brand_document(string pattern)
    {
        var document = host.AddDocument(Claas, JohnDeere);

        (await ActAsync(ClaasLemkenManager, string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, document)))
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unscoped_roles_act_on_foreign_brand_documents()
    {
        var approve = host.AddDocument(JohnDeere);
        (await ActAsync(Planner, $"/api/test/transfers/{approve}/approve")).ShouldBe(HttpStatusCode.OK);

        var cancel = host.AddDocument(JohnDeere);
        (await ActAsync(Administrator, $"/api/test/transfers/{cancel}/cancel")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_role_outside_the_policy_gets_403_before_the_document_is_looked_at()
    {
        var transfer = host.AddDocument(Claas);

        (await ActAsync(Keeper, $"/api/test/transfers/{transfer}/approve")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_gets_401_and_a_wrong_role_gets_403_both_as_problem_details()
    {
        using var browser = host.NewBrowser();

        using var anonymous = await browser.GetAsync("/api/test/audit");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Headers.Location.ShouldBeNull();
        (await Problem.ReadAsync(anonymous)).Code.ShouldBe(ProblemCodes.Unauthenticated);

        using var wrongRole = await browser.GetAsync("/api/test/audit", bearer: host.Token(Keeper));
        wrongRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await Problem.ReadAsync(wrongRole);
        problem.Code.ShouldBe(ProblemCodes.Forbidden);
        problem.TraceId.ShouldNotBeNullOrEmpty();

        using var allowed = await browser.GetAsync("/api/test/audit", bearer: host.Token(Administrator));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cookie_sessions_get_403_without_a_redirect_too()
    {
        host.Verifier.Add(Keeper);
        using var browser = host.NewBrowser();
        await browser.LoginOrFailAsync(Keeper);

        using var response = await browser.GetAsync("/api/test/audit");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Location.ShouldBeNull();
        (await Problem.ReadAsync(response)).Code.ShouldBe(ProblemCodes.Forbidden);
    }

    private async Task<HttpStatusCode> ActAsync(TestUser user, string path)
    {
        using var browser = host.NewBrowser();
        using var response = await browser.PostAsync(path, bearer: host.Token(user));
        return response.StatusCode;
    }

    private async Task<string?> StateAsync(int document)
    {
        using var browser = host.NewBrowser();
        using var response = await browser.GetAsync($"/api/test/documents/{document}", bearer: host.Token(Administrator));
        var body = await response.Content.ReadFromJsonAsync<DocumentBody>(TestContext.Current.CancellationToken);
        return body?.State;
    }

    private sealed record DocumentBody(string State);
}
