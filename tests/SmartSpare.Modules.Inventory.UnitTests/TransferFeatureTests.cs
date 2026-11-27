using SmartSpare.SharedKernel;
using System.Security.Claims;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Features.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// Who may do what with a transfer without a database: the state machine's candidate steps, the policy and brand scope
/// matrix of STAGE_3_PLAN 3.2–3.3, the request validators and the timeline.
/// </summary>
public sealed class TransferFeatureTests
{
    private const short Klp = 3;
    private const short Pnv = 5;
    private const short Jd = 1;
    private const short Claas = 2;
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Candidate_steps_follow_the_state_machine_and_the_home_hub()
    {
        var order = Proposed();
        TransferAccess.Candidates(order, HubRights.Everywhere).ShouldBe([TransferActions.Approve, TransferActions.Cancel]);

        order.Approve(Guid.NewGuid(), Now);
        TransferAccess.Candidates(order, HubRights.Everywhere).ShouldBe([TransferActions.Cancel, TransferActions.Dispatch]);
        TransferAccess.Candidates(order, HubRights.At(Klp)).ShouldBe([TransferActions.Cancel, TransferActions.Dispatch]);
        TransferAccess.Candidates(order, HubRights.At(Pnv)).ShouldBe([TransferActions.Cancel]);

        order.Dispatch(Guid.NewGuid(), HubRights.At(Klp), Now);
        TransferAccess.Candidates(order, HubRights.At(Pnv)).ShouldBe([TransferActions.Receive]);
        TransferAccess.Candidates(order, HubRights.At(Klp)).ShouldBeEmpty();

        order.Receive(Guid.NewGuid(), HubRights.At(Pnv), Now);
        TransferAccess.Candidates(order, HubRights.Everywhere).ShouldBeEmpty();
    }

    public static TheoryData<string, string, short[], bool> Matrix => new()
    {
        // Planners approve and cancel everything; brand managers only all-own-brand orders.
        { AppRoles.InventoryPlanner, TransferActions.Approve, [Jd, Claas], true },
        { AppRoles.InventoryPlanner, TransferActions.Cancel, [Claas], true },
        { AppRoles.BrandManager, TransferActions.Approve, [Jd], true },
        { AppRoles.BrandManager, TransferActions.Approve, [Jd, Claas], false },
        { AppRoles.BrandManager, TransferActions.Cancel, [Claas], false },
        { AppRoles.BrandManager, TransferActions.Approve, [], false },
        // Administrators cancel but never approve; keepers only move stock.
        { AppRoles.Administrator, TransferActions.Cancel, [Claas], true },
        { AppRoles.Administrator, TransferActions.Approve, [Jd], false },
        { AppRoles.Administrator, TransferActions.Dispatch, [Jd], true },
        { AppRoles.WarehouseKeeper, TransferActions.Dispatch, [Jd], true },
        { AppRoles.WarehouseKeeper, TransferActions.Receive, [Jd], true },
        { AppRoles.WarehouseKeeper, TransferActions.Approve, [Jd], false },
        { AppRoles.WarehouseKeeper, TransferActions.Cancel, [Jd], false },
        { AppRoles.InventoryPlanner, TransferActions.Dispatch, [Jd], false },
        { AppRoles.Sponsor, TransferActions.Approve, [Jd], false },
        { AppRoles.ServiceEngineer, TransferActions.Receive, [Jd], false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Policies_and_brand_scope_decide_each_action(string role, string action, short[] brands, bool allowed)
    {
        var access = Access(role, ownBrands: [Jd]);

        (await access.MayAsync(action, Proposed(), new TransferBrands(brands.ToHashSet()))).ShouldBe(allowed);
    }

    [Fact]
    public async Task Without_a_signed_in_user_nothing_is_allowed()
    {
        var access = Access(role: null, ownBrands: []);

        (await access.MayAsync(TransferActions.Approve, Proposed(), new TransferBrands(new HashSet<short> { Jd }))).ShouldBeFalse();
    }

    [Fact]
    public void A_valid_proposal_passes() =>
        new CreateTransferRequestValidator().TestValidate(
            new CreateTransferRequest(Klp, Pnv, false, [new CreateTransferLine(1042, 11), new CreateTransferLine(1043, 0.5m)])).IsValid.ShouldBeTrue();

    [Fact]
    public void Invalid_proposals_fail_on_their_field()
    {
        var validator = new CreateTransferRequestValidator();
        validator.TestValidate(new CreateTransferRequest(Klp, Klp, false, [new CreateTransferLine(1, 1)]))
            .ShouldHaveValidationErrorFor(r => r.ToWarehouseId);
        validator.TestValidate(new CreateTransferRequest(0, Pnv, false, [new CreateTransferLine(1, 1)]))
            .ShouldHaveValidationErrorFor(r => r.FromWarehouseId);
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [])).ShouldHaveValidationErrorFor(r => r.Lines);
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [new CreateTransferLine(1, 1), new CreateTransferLine(1, 2)]))
            .ShouldHaveValidationErrorFor(r => r.Lines);
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [.. Enumerable.Range(1, 51).Select(i => new CreateTransferLine(i, 1))]))
            .ShouldHaveValidationErrorFor(r => r.Lines);
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [new CreateTransferLine(1, 0)])).IsValid.ShouldBeFalse();
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [new CreateTransferLine(0, 1)])).IsValid.ShouldBeFalse();
        validator.TestValidate(new CreateTransferRequest(Klp, Pnv, false, [new CreateTransferLine(1, 1.0001m)])).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, null, 1, 25, true)]
    [InlineData("approved", (short)3, 2, 100, true)]
    [InlineData("open", null, 1, 25, false)]
    [InlineData(null, (short)0, 1, 25, false)]
    [InlineData(null, null, 0, 25, false)]
    [InlineData(null, null, 1, 101, false)]
    public void The_transfer_list_query_is_validated(string? status, short? warehouse, int page, int pageSize, bool valid) =>
        new ListTransfersQueryValidator().TestValidate(new ListTransfersQuery(status, warehouse, page, pageSize)).IsValid.ShouldBe(valid);

    [Fact]
    public void The_create_command_applies_the_request_rules()
    {
        var validator = new CreateTransferCommandValidator();
        validator.TestValidate(new CreateTransferCommand(new CreateTransferRequest(Klp, Pnv, true, [new CreateTransferLine(1042, 2)]))).IsValid.ShouldBeTrue();
        validator.TestValidate(new CreateTransferCommand(new CreateTransferRequest(Klp, Klp, true, [new CreateTransferLine(1042, 2)]))).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Customer cancelled", true)]
    [InlineData("", false)]
    public void A_cancellation_needs_a_reason(string reason, bool valid)
    {
        new CancelTransferRequestValidator().TestValidate(new CancelTransferRequest(reason)).IsValid.ShouldBe(valid);
        new CancelTransferCommandValidator().TestValidate(new CancelTransferCommand(1, reason)).IsValid.ShouldBe(valid);
        new CancelTransferRequestValidator().TestValidate(new CancelTransferRequest(new string('r', 301))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_timeline_lists_the_steps_taken_in_order()
    {
        var planner = Guid.NewGuid();
        var keeper = Guid.NewGuid();
        var order = Proposed();
        order.Approve(planner, Now);
        order.Dispatch(keeper, HubRights.At(Klp), Now.AddHours(1));

        TransferDetailsReader.Timeline(order).Select(s => (s.Step, s.UserId)).ShouldBe(
            [("requested", (Guid?)Requester), ("approved", planner), ("dispatched", keeper)]);

        var cancelled = Proposed();
        cancelled.Cancel("No longer needed", Now);
        TransferDetailsReader.Timeline(cancelled).Select(s => s.Step).ShouldBe(["requested", "cancelled"]);
    }

    private static readonly Guid Requester = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static TransferOrder Proposed() =>
        TransferOrder.Propose(Klp, Pnv, false, Requester, [new TransferLineDraft(1042, 11)]).Value;

    private static TransferAccess Access(string? role, short[] ownBrands)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddPermissionAuthorization();
        services.AddBrandScopeAuthorization();
        var provider = services.BuildServiceProvider();

        var claims = new List<Claim> { new(AppClaimTypes.Subject, Guid.NewGuid().ToString()) };
        if (role is not null)
        {
            claims.Add(new Claim(AppClaimTypes.Role, role));
            claims.AddRange(DefaultRoles.Bundles[role].Select(p => new Claim(AppClaimTypes.Permission, p)));   // what the BFF adds (ADR 0028)
        }

        claims.AddRange(ownBrands.Select(b => new Claim(AppClaimTypes.Brand, b.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var principal = role is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(claims, "test", AppClaimTypes.Name, AppClaimTypes.Role));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql("Host=offline.invalid").Options);
        return new TransferAccess(db, provider.GetRequiredService<IAuthorizationService>(), http, new ClaimsCurrentUser(http));
    }
}
