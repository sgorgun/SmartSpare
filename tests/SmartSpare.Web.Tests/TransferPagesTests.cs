using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The transfer screens (STAGE_5_PLAN section 6): the list with statuses, the detail that offers exactly the server's
/// allowedActions with a confirmation, the reason a cancellation needs, server refusals with their reference, and the
/// proposal form validated by the shared validator.
/// </summary>
public sealed class TransferPagesTests : ClientTestContext
{
    private readonly FakeTransferClient _inventory = new();

    public TransferPagesTests()
    {
        Services.AddScoped<InventoryClient>(_ => _inventory);
        Services.AddLogging();
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_list_shows_each_order_with_route_and_status()
    {
        var page = Render<TransfersPage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(1));
        page.Markup.ShouldContain("TR-2026-000412");
        page.Markup.ShouldContain("KLP → PNV");
        page.FindAll("[aria-label='Status: Proposed']").ShouldHaveSingleItem();
    }

    [Fact]
    public void Only_the_allowed_actions_are_offered()
    {
        _inventory.Details = FakeTransferClient.Transfer(TransferStatuses.Approved, [TransferActions.Dispatch]);

        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 412L));

        page.WaitForAssertion(() => page.Find("[data-testid=action-dispatch]"));
        page.FindAll("[data-testid=action-approve]").ShouldBeEmpty();
        page.FindAll("[data-testid=action-cancel]").ShouldBeEmpty();
        page.Find("[data-testid=transfer-timeline]").TextContent.ShouldContain("Requested");
    }

    [Fact]
    public void Without_allowed_actions_there_is_no_action_bar()
    {
        _inventory.Details = FakeTransferClient.Transfer(TransferStatuses.Received, []);

        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 412L));

        page.WaitForAssertion(() => page.Markup.ShouldContain("TR-2026-000412"));
        page.FindAll("[data-testid=transfer-actions]").ShouldBeEmpty();
    }

    [Fact]
    public void Approval_is_confirmed_first_and_then_shows_the_new_status()
    {
        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 412L));
        page.WaitForAssertion(() => page.Find("[data-testid=action-approve]"));

        page.Find("[data-testid=action-approve]").Click();
        _inventory.Steps.ShouldBeEmpty("nothing happens before the confirmation");
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => _inventory.Steps.ShouldHaveSingleItem().ShouldBe((412L, TransferActions.Approve, (string?)null)));
        page.WaitForAssertion(() => page.Find("[data-testid=transfer-status]").TextContent.ShouldContain("Approved"));
    }

    [Fact]
    public void A_refused_approval_shows_the_servers_reason_with_its_reference()
    {
        _inventory.NextStep = ApiResult.Failure<TransferDetails>(new ProblemInfo(409, "SS201", "Conflict",
            "Donor warehouse 3 would fall below its safety stock for part 1042", "ref-201"));
        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 412L));
        page.WaitForAssertion(() => page.Find("[data-testid=action-approve]"));

        page.Find("[data-testid=action-approve]").Click();
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=action-error]").TextContent
            .ShouldBe("Donor warehouse 3 would fall below its safety stock for part 1042 Reference: ref-201"));
    }

    [Fact]
    public void A_cancellation_needs_a_reason()
    {
        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 412L));
        page.WaitForAssertion(() => page.Find("[data-testid=action-cancel]"));

        page.Find("[data-testid=action-cancel]").Click();
        page.Find("[data-testid=confirm]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=action-error]").TextContent.ShouldContain("Reason"));
        _inventory.Steps.ShouldBeEmpty();

        page.Find("input[data-testid=cancel-reason], [data-testid=cancel-reason] input").Change("Customer cancelled");
        page.Find("[data-testid=confirm]").Click();
        page.WaitForAssertion(() => _inventory.Steps.ShouldHaveSingleItem().ShouldBe((412L, TransferActions.Cancel, (string?)"Customer cancelled")));
    }

    [Fact]
    public void A_proposal_without_parts_is_refused_before_the_request()
    {
        var page = Render<NewTransferPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=propose]"));

        page.Find("[data-testid=propose]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=new-transfer-error]").TextContent.ShouldBe("Choose a part for every line."));
        _inventory.Created.ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_transfer_shows_the_servers_message()
    {
        _inventory.DetailsResult = ApiResult.Failure<TransferDetails>(
            new ProblemInfo(404, "Inventory.TransferNotFound", "Resource not found", "The transfer order does not exist.", "t404"));

        var page = Render<TransferDetailPage>(p => p.Add(x => x.Id, 9L));

        page.WaitForAssertion(() => page.Find("[data-testid=transfer-error]").TextContent.ShouldBe("The transfer order does not exist. Reference: t404"));
    }
}

/// <summary>An <see cref="InventoryClient"/> with one transfer order whose state the test decides.</summary>
public sealed class FakeTransferClient() : InventoryClient(new HttpClient())
{
    public TransferDetails Details { get; set; } = Transfer(TransferStatuses.Proposed, [TransferActions.Approve, TransferActions.Cancel]);

    public ApiResult<TransferDetails>? DetailsResult { get; set; }

    public ApiResult<TransferDetails>? NextStep { get; set; }

    public List<(long Id, string Action, string? Reason)> Steps { get; } = [];

    public List<CreateTransferRequest> Created { get; } = [];

    public static TransferDetails Transfer(string status, IReadOnlyList<string> allowed) => new(
        new TransferSummary(412, "TR-2026-000412", 3, "KLP", 5, "PNV", status, false, 1, 11, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        [new TransferLineResponse(907, 1042, "CLAAS", "CP-4821", "Heavy-duty straw chopper blade", "PCS", 11, null, null, 20)],
        [new TransferStep("requested", Guid.NewGuid(), DateTimeOffset.UtcNow)],
        null,
        allowed);

    public override Task<ApiResult<IReadOnlyList<WarehouseResponse>>> ListWarehousesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success<IReadOnlyList<WarehouseResponse>>([]));

    public override Task<ApiResult<PagedResponse<TransferSummary>>> ListTransfersAsync(
        string? status, short? warehouseId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(ApiResult.Success(new PagedResponse<TransferSummary>([Details.Order], page, pageSize, 1)));

    public override Task<ApiResult<TransferDetails>> GetTransferAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(DetailsResult ?? ApiResult.Success(Details));

    public override Task<ApiResult<TransferDetails>> TransferStepAsync(long id, string action, string? reason = null, CancellationToken cancellationToken = default)
    {
        Steps.Add((id, action, reason));
        if (NextStep is { } next)
        {
            return Task.FromResult(next);
        }

        var status = action switch
        {
            TransferActions.Approve => TransferStatuses.Approved,
            TransferActions.Dispatch => TransferStatuses.Dispatched,
            TransferActions.Receive => TransferStatuses.Received,
            _ => TransferStatuses.Cancelled,
        };
        return Task.FromResult(ApiResult.Success(Transfer(status, [])));
    }

    public override Task<ApiResult<TransferDetails>> CreateTransferAsync(CreateTransferRequest request, CancellationToken cancellationToken = default)
    {
        Created.Add(request);
        return Task.FromResult(ApiResult.Success(Details));
    }
}
