using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Infrastructure;

/// <summary>The session context taken from the request's claims (#11, STAGE_3_PLAN section 6).</summary>
public sealed class CurrentUserTests
{
    [Theory]
    [InlineData("SmartSpare.Cookie")]
    [InlineData(InternalTokenDefaults.AuthenticationType)]
    public void The_claims_of_either_scheme_give_the_same_user(string authenticationType)
    {
        var id = Guid.NewGuid();
        var user = CurrentUser(new ClaimsIdentity(
            [
                new Claim(AppClaimTypes.Subject, id.ToString()),
                new Claim(AppClaimTypes.Role, AppRoles.WarehouseKeeper),
                new Claim(AppClaimTypes.Role, AppRoles.Sponsor),
                new Claim(AppClaimTypes.Warehouse, "4"),
            ],
            authenticationType));

        user.UserId.ShouldBe(id);
        user.Roles.ShouldBe([AppRoles.WarehouseKeeper, AppRoles.Sponsor], ignoreOrder: true);
        user.HomeWarehouseId.ShouldBe((short)4);
    }

    [Fact]
    public void An_anonymous_request_or_no_request_has_no_user()
    {
        var anonymous = CurrentUser(new ClaimsIdentity([new Claim(AppClaimTypes.Subject, Guid.NewGuid().ToString())]));
        var noRequest = new ClaimsCurrentUser(new HttpContextAccessor());

        foreach (var user in new ICurrentUser[] { anonymous, noRequest })
        {
            user.UserId.ShouldBeNull();
            user.Roles.ShouldBeEmpty();
            user.HomeWarehouseId.ShouldBeNull();
        }
    }

    [Fact]
    public void The_role_list_is_sorted_distinct_and_drops_values_that_could_break_it()
    {
        SessionContextInterceptor.RolesValue(["SPONSOR", "BRAND_MANAGER", "SPONSOR", "BAD,ROLE", " "])
            .ShouldBe("BRAND_MANAGER,SPONSOR");
        SessionContextInterceptor.RolesValue([]).ShouldBeEmpty();
    }

    private static ClaimsCurrentUser CurrentUser(ClaimsIdentity identity) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } });
}
