using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Infrastructure;

/// <summary><see cref="BrandScopedActionHandler"/> for the cases of STAGE_3_PLAN section 9.1, with the default role bundles (ADR 0028).</summary>
public sealed class BrandScopeTests
{
    private const short Claas = 1;
    private const short JohnDeere = 2;
    private const short Lemken = 5;

    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddAuthorization()
        .AddBrandScopeAuthorization()
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    public static TheoryData<string> ScopedRequirements => [.. BrandScopeRequirements.ByPermission.Keys];

    [Theory]
    [MemberData(nameof(ScopedRequirements))]
    public async Task A_brand_manager_may_act_on_documents_with_only_own_brands(string policy) =>
        (await AllowedAsync(User([AppRoles.BrandManager], Claas, Lemken), policy, Claas, Lemken, Claas)).ShouldBeTrue();

    [Theory]
    [MemberData(nameof(ScopedRequirements))]
    public async Task One_foreign_line_among_own_lines_forbids_the_action(string policy) =>
        (await AllowedAsync(User([AppRoles.BrandManager], Claas, Lemken), policy, Claas, JohnDeere)).ShouldBeFalse();

    [Theory]
    [MemberData(nameof(ScopedRequirements))]
    public async Task A_document_without_lines_is_denied(string policy) =>
        (await AllowedAsync(User([AppRoles.BrandManager], Claas), policy)).ShouldBeFalse();

    [Theory]
    [MemberData(nameof(ScopedRequirements))]
    public async Task A_brand_manager_without_brand_claims_is_denied(string policy) =>
        (await AllowedAsync(User([AppRoles.BrandManager]), policy, Claas)).ShouldBeFalse();

    [Theory]
    [MemberData(nameof(ScopedRequirements))]
    public async Task A_role_outside_the_requirement_is_denied(string policy) =>
        (await AllowedAsync(User([AppRoles.WarehouseKeeper], Claas), policy, Claas)).ShouldBeFalse();

    [Fact]
    public async Task A_sponsor_alone_approves_foreign_brand_purchase_orders() =>
        (await AllowedAsync(User([AppRoles.Sponsor]), Permissions.Procurement.PurchaseOrder.Approve, JohnDeere)).ShouldBeTrue();

    [Fact]
    public async Task Brand_manager_and_sponsor_approve_a_foreign_brand_purchase_order_as_sponsor() =>
        (await AllowedAsync(User([AppRoles.BrandManager, AppRoles.Sponsor], Claas), Permissions.Procurement.PurchaseOrder.Approve, JohnDeere)).ShouldBeTrue();

    [Fact]
    public async Task Sponsor_grants_nothing_for_transfers_so_the_brand_scope_still_applies() =>
        (await AllowedAsync(User([AppRoles.BrandManager, AppRoles.Sponsor], Claas), Permissions.Inventory.Transfer.Approve, JohnDeere)).ShouldBeFalse();

    [Fact]
    public async Task An_inventory_planner_approves_foreign_brand_transfers() =>
        (await AllowedAsync(User([AppRoles.InventoryPlanner]), Permissions.Inventory.Transfer.Approve, JohnDeere)).ShouldBeTrue();

    [Theory]
    [InlineData(Permissions.Inventory.Transfer.Cancel)]
    [InlineData(Permissions.Procurement.PurchaseOrder.Cancel)]
    public async Task An_administrator_cancels_foreign_brand_documents(string policy) =>
        (await AllowedAsync(User([AppRoles.Administrator]), policy, JohnDeere)).ShouldBeTrue();

    [Theory]
    [InlineData(Permissions.Inventory.Transfer.Cancel)]
    [InlineData(Permissions.Procurement.PurchaseOrder.Cancel)]
    public async Task A_brand_manager_cannot_cancel_a_mixed_brand_document(string policy) =>
        (await AllowedAsync(User([AppRoles.BrandManager], Claas), policy, Claas, JohnDeere)).ShouldBeFalse();

    private static async Task<bool> AllowedAsync(ClaimsPrincipal user, string policy, params short[] lineBrands)
    {
        var result = await Authorization.AuthorizeAsync(user, new Document(lineBrands.ToHashSet()), BrandScopeRequirements.ByPermission[policy]);
        return result.Succeeded;
    }

    private static ClaimsPrincipal User(string[] roles, params short[] brands) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(AppClaimTypes.Role, r))
                .Concat(DefaultRoles.Of(roles).Select(p => new Claim(AppClaimTypes.Permission, p)))   // what the BFF adds (ADR 0028)
                .Concat(brands.Select(b => new Claim(AppClaimTypes.Brand, b.ToString(CultureInfo.InvariantCulture)))),
            "test",
            AppClaimTypes.Name,
            AppClaimTypes.Role));

    private sealed record Document(IReadOnlySet<short> BrandIds) : IBrandScoped;
}
