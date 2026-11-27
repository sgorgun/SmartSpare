using System.Net;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Web.Client.Http;
using SmartSpare.Web.Client.Layout;
using SmartSpare.Web.Client.Shared;

namespace SmartSpare.Web.Tests;

/// <summary>The menu by policy, the sign-in redirect and the HTTP handlers (STAGE_3_PLAN 9.3, 1.4, 8.5).</summary>
public sealed class NavigationAndHttpTests : ClientTestContext
{
    public NavigationAndHttpTests()
    {
        // The real policies and authorization service. bUnit pre-registers a placeholder service that AddAuthorizationCore
        // (TryAdd) would leave in place, so the default service is registered explicitly.
        Services.AddLogging();
        Services.AddPermissionAuthorization();
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
    }

    public static TheoryData<string, string[], string[]> MenuCases => new()
    {
        { AppRoles.WarehouseKeeper, ["inventory", "fleet", "procurement", "reporting"], ["forecasting"] },
        { AppRoles.BrandManager, ["inventory", "fleet", "procurement", "forecasting", "reporting"], [] },
        { AppRoles.ServiceEngineer, ["inventory", "fleet", "reporting"], ["procurement", "forecasting"] },
        { AppRoles.Sponsor, ["inventory", "fleet", "procurement", "reporting"], ["forecasting"] },
    };

    [Theory]
    [MemberData(nameof(MenuCases))]
    public void The_menu_shows_the_areas_the_roles_may_work_in(string role, string[] shown, string[] hidden)
    {
        var menu = Render<NavMenu>(p => p.AddCascadingValue(State(User("someone", role))));

        var links = menu.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
        foreach (var area in shown)
        {
            links.ShouldContain(area, $"{role} should see {area}");
        }

        foreach (var area in hidden)
        {
            links.ShouldNotContain(area, $"{role} should not see {area}");
        }
    }

    [Fact]
    public void An_anonymous_user_sees_no_area()
    {
        var menu = Render<NavMenu>(p => p.AddCascadingValue(State(Anonymous)));

        menu.FindAll("a").Select(a => a.GetAttribute("href")).ShouldBe([""]);
    }

    [Fact]
    public void RedirectToLogin_keeps_the_return_url_and_reloads()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("inventory?tab=stock");

        Render<RedirectToLogin>();

        var last = navigation.History.First();
        last.Uri.ShouldBe("login?returnUrl=%2Finventory%3Ftab%3Dstock");
        last.Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public async Task A_401_reloads_to_the_sign_in_page()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("procurement");
        using var client = Client(new StubHandler(HttpStatusCode.Unauthorized));

        using var response = await client.GetAsync(new Uri("api/procurement/ping", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var last = navigation.History.First();
        last.Uri.ShouldBe("login?returnUrl=%2Fprocurement");
        last.Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public async Task A_403_is_shown_in_the_snackbar_with_the_trace_id()
    {
        var snackbar = Services.GetRequiredService<ISnackbar>();
        using var client = Client(new StubHandler(HttpStatusCode.Forbidden,
            new { status = 403, code = ProblemCodes.Forbidden, title = "Forbidden", traceId = "0af7651916cd43dd8448eb211c80319c" }));

        using var response = await client.GetAsync(new Uri("api/test/audit", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        var shown = snackbar.ShownSnackbars.ShouldHaveSingleItem();
        shown.Message.ShouldBe("You are not allowed to do this. Reference: 0af7651916cd43dd8448eb211c80319c");
        shown.Severity.ShouldBe(Severity.Error);
        Services.GetRequiredService<BunitNavigationManager>().History.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("GET", false)]
    public async Task Unsafe_requests_carry_the_antiforgery_token(string method, bool expected)
    {
        var stub = new StubHandler(HttpStatusCode.NoContent);
        using var handler = new AntiforgeryHandler(new FixedAntiforgery("request-token")) { InnerHandler = stub };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        using var request = new HttpRequestMessage(new HttpMethod(method), "bff/logout");
        using var response = await client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        stub.Request!.Headers.TryGetValues(HttpHeaderNames.Antiforgery, out var values).ShouldBe(expected);
        if (expected)
        {
            values.ShouldBe(["request-token"]);
        }
    }

    [Fact]
    public void Problem_messages_name_the_known_codes_and_always_the_reference()
    {
        var messages = Services.GetRequiredService<ProblemMessages>();

        messages.Describe(new ProblemInfo(400, ProblemCodes.AntiforgeryFailed, null, null, "abc"))
            .ShouldBe("The form has expired. Reload the page and try again. Reference: abc");
        messages.Describe(new ProblemInfo(429, ProblemCodes.TooManyRequests, null, null, null))
            .ShouldBe("Too many attempts. Wait a minute and try again.");
        messages.Describe(new ProblemInfo(422, "SS201", null, "Donor warehouse would fall below its safety stock.", "def"))
            .ShouldBe("Donor warehouse would fall below its safety stock. Reference: def");
        messages.Describe(new ProblemInfo(500, null, "Unexpected error", "stack details", "ghi"))
            .ShouldBe("Something went wrong. Please try again. Reference: ghi", "server details of a 500 are not shown");
    }

    [Fact]
    public void A_status_chip_has_an_icon_and_text_not_colour_alone()
    {
        var chip = Render<StatusChip>(p => p
            .Add(x => x.Label, "Approved")
            .Add(x => x.Icon, Icons.Material.Filled.CheckCircle)
            .Add(x => x.Color, Color.Success));

        chip.Markup.ShouldContain("Approved");
        chip.Find("svg").ShouldNotBeNull();
        chip.Find("[aria-label]").GetAttribute("aria-label").ShouldBe("Status: Approved");
    }

    private HttpClient Client(StubHandler stub)
    {
#pragma warning disable CA2000 // Disposed with the HttpClient.
        var handler = new UnauthorizedHandler(
            Services.GetRequiredService<NavigationManager>(), Services.GetRequiredService<ISnackbar>(), Services.GetRequiredService<ProblemMessages>())
        {
            InnerHandler = stub,
        };
#pragma warning restore CA2000
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
    }
}
