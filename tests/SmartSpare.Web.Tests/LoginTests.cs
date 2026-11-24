using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>The sign-in page (STAGE_3_PLAN 9.3, 1.4, 8.3).</summary>
public sealed class LoginTests : ClientTestContext
{
    private readonly FakeIdentityClient _identity = new();

    public LoginTests()
    {
        Services.AddSingleton<IdentityClient>(_identity);
        UseWebAssemblyRenderer();
    }

    [Fact]
    public async Task Empty_fields_show_the_shared_validator_messages_and_send_nothing()
    {
        var page = RenderLogin();

        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Sign in", StringComparison.Ordinal)).Click());

        page.WaitForAssertion(() => page.Markup.ShouldContain("must not be empty"));
        _identity.Logins.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_signing_in_the_button_is_busy_and_disabled()
    {
        var pending = new TaskCompletionSource<ApiResult<NoContent>>();
        _identity.OnLogin = _ => pending.Task;
        var page = RenderLogin();
        Fill(page, "tomas.radzevicius", "a password");

        var submit = page.InvokeAsync(() => SubmitButton(page).Click());

        page.WaitForAssertion(() =>
        {
            SubmitButton(page).HasAttribute("disabled").ShouldBeTrue();
            page.Markup.ShouldContain("Signing in");
        });
        pending.SetResult(ApiResult.Success(new NoContent()));
        await submit;
    }

    [Fact]
    public async Task A_failed_sign_in_shows_the_generic_message_with_its_reference()
    {
        _identity.OnLogin = _ => Task.FromResult(ApiResult.Failure<NoContent>(
            new ProblemInfo(400, ProblemCodes.InvalidCredentials, "Sign-in failed", "The user name or password is incorrect.", "4bf92f3577b34da6a3ce929d0e0e4736")));
        var page = RenderLogin();
        Fill(page, "tomas.radzevicius", "wrong");

        await page.InvokeAsync(() => SubmitButton(page).Click());

        page.WaitForAssertion(() =>
        {
            var alert = page.Find("[role=alert]");
            alert.TextContent.ShouldContain("The user name or password is incorrect.");
            alert.TextContent.ShouldContain("Reference: 4bf92f3577b34da6a3ce929d0e0e4736");
        });
        Services.GetRequiredService<BunitNavigationManager>().History.Count.ShouldBe(1, "no navigation after a failure");
    }

    [Fact]
    public async Task A_successful_sign_in_reloads_the_return_url()
    {
        var page = RenderLogin(returnUrl: "/inventory?tab=stock");
        Fill(page, "  tomas.radzevicius ", "a password");

        await page.InvokeAsync(() => SubmitButton(page).Click());

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        page.WaitForAssertion(() => navigation.History.Count.ShouldBe(2));
        var last = navigation.History.First();
        last.Uri.ShouldBe("/inventory?tab=stock");
        last.Options.ForceLoad.ShouldBeTrue("the auth state of the app is fixed until the next load (STAGE_3_PLAN 1.4)");
        _identity.Logins.ShouldHaveSingleItem().UserName.ShouldBe("tomas.radzevicius");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example")]
    public async Task An_external_return_url_is_replaced_by_the_home_page(string returnUrl)
    {
        var page = RenderLogin(returnUrl);
        Fill(page, "tomas.radzevicius", "a password");

        await page.InvokeAsync(() => SubmitButton(page).Click());

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        page.WaitForAssertion(() =>
        {
            navigation.History.Count.ShouldBe(2);
            navigation.History.First().Uri.ShouldBe("/");
        });
    }

    private IRenderedComponent<Login> RenderLogin(string? returnUrl = null)
    {
        // The page is opened by navigation, so [SupplyParameterFromQuery] gets the return URL; History then holds one entry.
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo(returnUrl is null ? "login" : "login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        return Render<Login>(p => p.AddCascadingValue(State(Anonymous)));
    }

    private static AngleSharp.Dom.IElement SubmitButton(IRenderedComponent<Login> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Sign in", StringComparison.Ordinal) || b.TextContent.Contains("Signing in", StringComparison.Ordinal));

    private static void Fill(IRenderedComponent<Login> page, string userName, string password)
    {
        page.Find("input[autocomplete=username]").Input(userName);
        page.Find("input[autocomplete=current-password]").Input(password);
    }
}
