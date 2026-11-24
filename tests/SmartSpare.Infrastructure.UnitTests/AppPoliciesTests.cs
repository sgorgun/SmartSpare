using System.Globalization;
using System.Security.Claims;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;

namespace SmartSpare.Infrastructure;

/// <summary><see cref="AppPolicies"/> evaluated with the real authorization service, and the shared sign-in validator.</summary>
public sealed class AppPoliciesTests
{
    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddAuthorization(AppPolicies.Register)
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    public static TheoryData<string, string> PolicyRoleCombinations()
    {
        var data = new TheoryData<string, string>();
        foreach (var policy in AppPolicies.Roles.Keys)
        {
            foreach (var role in AppRoles.All)
            {
                data.Add(policy, role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PolicyRoleCombinations))]
    public async Task A_policy_allows_exactly_its_roles(string policy, string role)
    {
        var result = await Authorization.AuthorizeAsync(User([role]), policy);

        result.Succeeded.ShouldBe(AppPolicies.Roles[policy].Contains(role));
    }

    [Theory]
    [MemberData(nameof(PolicyRoleCombinations))]
    public async Task Policies_never_look_at_brands(string policy, string role)
    {
        // Brand scope is a resource check on approvals only (section 3.3); the role gate of a policy, and every read,
        // gives the same answer with or without brand claims.
        var withoutBrands = await Authorization.AuthorizeAsync(User([role]), policy);
        var withBrands = await Authorization.AuthorizeAsync(User([role], 1, 2, 5), policy);

        withBrands.Succeeded.ShouldBe(withoutBrands.Succeeded);
    }

    [Fact]
    public async Task The_fallback_policy_requires_an_authenticated_user()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var options = new AuthorizationOptions();
        AppPolicies.Register(options);

        (await Authorization.AuthorizeAsync(anonymous, options.FallbackPolicy!)).Succeeded.ShouldBeFalse();
        (await Authorization.AuthorizeAsync(User([]), options.FallbackPolicy!)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Anonymous_users_pass_no_policy()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AppClaimTypes.Role, AppRoles.Administrator)]));

        foreach (var policy in AppPolicies.Roles.Keys)
        {
            (await Authorization.AuthorizeAsync(anonymous, policy)).Succeeded.ShouldBeFalse(policy);
        }
    }

    [Theory]
    [InlineData("tomas.radzevicius", "any password", true)]
    [InlineData("", "any password", false)]
    [InlineData("tomas.radzevicius", "", false)]
    [InlineData("a-name-that-is-longer-than-the-fifty-characters-allowed", "x", false)]
    public void The_sign_in_validator_checks_presence_and_length_only(string userName, string password, bool valid)
    {
        var result = new LoginRequestValidator().TestValidate(new LoginRequest(userName, password));

        result.IsValid.ShouldBe(valid);
    }

    [Fact]
    public void The_sign_in_validator_caps_the_password_length()
    {
        new LoginRequestValidator().TestValidate(new LoginRequest("tomas", new string('x', LoginRequestValidator.MaxPasswordLength + 1)))
            .ShouldHaveValidationErrorFor(r => r.Password);
    }

    private static ClaimsPrincipal User(string[] roles, params short[] brands) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(AppClaimTypes.Role, r))
                .Concat(brands.Select(b => new Claim(AppClaimTypes.Brand, b.ToString(CultureInfo.InvariantCulture))))
                .Append(new Claim(AppClaimTypes.Subject, Guid.NewGuid().ToString())),
            "test",
            AppClaimTypes.Name,
            AppClaimTypes.Role));
}
