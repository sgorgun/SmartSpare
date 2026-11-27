using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Web.Client.Features.Fleet;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Tests;

/// <summary>
/// The maintenance and service-order screens (STAGE_6_PLAN section 6, PR 6k): the detail offers exactly the server's
/// allowedActions, completes with engine hours and cancels with a reason, shows a conflict as a sentence and the current
/// state, follows the kit while Inventory answers ("Reserving parts…" until reserved, then stops; a hint when it never
/// answers), shows shortfalls; the lists filter on the server; the due list offers "Create order" only where it can be
/// created, and the form sends a planned service or a repair.
/// </summary>
public sealed class ServiceOrderPagesTests : ClientTestContext
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeFleetClient _fleet = new();

    private readonly FakeTimeProvider _time = new(Now);

    public ServiceOrderPagesTests()
    {
        Services.AddScoped<FleetClient>(_ => _fleet);
        Services.AddScoped<InventoryClient>(_ => new FakeWarehouses());
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddLogging();
        Services.AddAuthorizationCore(AppPolicies.Register);
        Services.AddTransient<IAuthorizationService, DefaultAuthorizationService>();
        UseWebAssemblyRenderer();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void The_detail_offers_exactly_the_allowed_actions()
    {
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.InProgress, KitStatuses.Reserved, [ServiceOrderActions.Complete])));

        var page = RenderOrder();

        page.WaitForAssertion(() => page.Find("[data-testid=action-complete]"));
        page.FindAll("[data-testid=action-start]").ShouldBeEmpty();
        page.FindAll("[data-testid=action-cancel]").ShouldBeEmpty();
        page.FindAll("[aria-label='Status: In progress']").ShouldHaveSingleItem();
    }

    [Fact]
    public void A_closed_order_has_no_action_bar()
    {
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Cancelled, KitStatuses.Released, [])));

        var page = RenderOrder();

        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: Kit released']").ShouldHaveSingleItem());
        page.FindAll("[data-testid=order-actions]").ShouldBeEmpty();
    }

    [Fact]
    public void Starting_asks_first_and_then_shows_the_new_state()
    {
        var page = RenderOrder();
        page.WaitForAssertion(() => page.Find("[data-testid=action-start]"));

        page.Find("[data-testid=action-start]").Click();
        page.Find("[data-testid=confirm-text]").TextContent.ShouldBe("Start the work on SO-2026-000501?");
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: In progress']").ShouldHaveSingleItem());
        _fleet.Actions.ShouldHaveSingleItem().ShouldBe((501L, ServiceOrderActions.Start, (decimal?)null, (string?)null));
    }

    [Fact]
    public void Completing_is_prefilled_with_the_machines_hours_and_sends_what_the_engineer_enters()
    {
        var page = RenderOrder();
        page.WaitForAssertion(() => page.Find("[data-testid=action-complete]"));

        page.Find("[data-testid=action-complete]").Click();

        page.WaitForAssertion(() => page.Find("input[data-testid=complete-hours]").GetAttribute("value").ShouldBe("1492"));
        page.Find("input[data-testid=complete-hours]").GetAttribute("inputmode").ShouldBe("decimal");
        page.Markup.ShouldContain("The machine is at 1,492 h.");
        page.Find("input[data-testid=complete-hours]").Change("1495,5");
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => _fleet.Actions.ShouldHaveSingleItem().ShouldBe((501L, ServiceOrderActions.Complete, (decimal?)1495.5m, (string?)null)));
    }

    [Fact]
    public void Cancelling_needs_a_reason_before_anything_is_sent()
    {
        var page = RenderOrder();
        page.WaitForAssertion(() => page.Find("[data-testid=action-cancel]"));

        page.Find("[data-testid=action-cancel]").Click();
        page.Find("[data-testid=confirm]").Click();

        page.Find("[data-testid=action-error]").TextContent.ShouldBe("Give a reason for the cancellation (at most 300 characters).");
        _fleet.Actions.ShouldBeEmpty();

        page.Find("textarea[data-testid=cancel-reason]").Change("Customer sold the machine");
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => _fleet.Actions.ShouldHaveSingleItem().Reason.ShouldBe("Customer sold the machine"));
        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: Kit released']").ShouldHaveSingleItem());
    }

    [Fact]
    public void A_conflict_reads_as_the_servers_sentence_and_the_page_shows_the_current_state()
    {
        _fleet.NextAction = ApiResult.Failure<ServiceOrderDetails>(new ProblemInfo(
            409, "SS300", "Conflict", "A service order that is cancelled cannot be started.", "7c2d1e0f9a8b4c3d2e1f0a9b8c7d6e5f"));
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.PartsReserved, KitStatuses.Reserved, [ServiceOrderActions.Start])));
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Cancelled, KitStatuses.Released, [])));
        var page = RenderOrder();
        page.WaitForAssertion(() => page.Find("[data-testid=action-start]"));

        page.Find("[data-testid=action-start]").Click();
        page.Find("[data-testid=confirm]").Click();

        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: Cancelled']").ShouldHaveSingleItem());
        page.FindAll("[data-testid=order-actions]").ShouldBeEmpty("the order moved on: no action is left");
    }

    [Fact]
    public void While_the_kit_is_being_reserved_the_page_asks_again_until_the_hub_answers()
    {
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Draft, KitStatuses.Reserving, [])));
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Draft, KitStatuses.Reserving, [])));
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.PartsReserved, KitStatuses.Reserved, [ServiceOrderActions.Start])));
        var page = RenderOrder();

        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: Reserving parts…']").ShouldHaveSingleItem());
        page.Find("[data-testid=order-refreshing]");
        page.Find("[data-testid=order-kit]").GetAttribute("aria-live").ShouldBe("polite");

        _time.Advance(KitRefresh.Interval);
        page.WaitForAssertion(() => _fleet.OrderReads.ShouldBe(2));
        _time.Advance(KitRefresh.Interval);

        page.WaitForAssertion(() => page.FindAll("[aria-label='Status: Kit reserved']").ShouldHaveSingleItem());
        page.FindAll("[data-testid=order-refreshing]").ShouldBeEmpty();
        _time.Advance(KitRefresh.Interval * 3);
        _fleet.OrderReads.ShouldBe(3, "the page stops asking once the hub answered");
    }

    [Fact]
    public void When_the_hub_never_answers_the_page_stops_asking_and_says_so()
    {
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Draft, KitStatuses.Reserving, [])));
        var page = RenderOrder();
        page.WaitForAssertion(() => page.Find("[data-testid=order-refreshing]"));

        for (var i = 0; i < 16; i++)
        {
            _time.Advance(KitRefresh.Interval);
            page.WaitForAssertion(() => _fleet.OrderReads.ShouldBeGreaterThan(i));
        }

        page.WaitForAssertion(() => page.Find("[data-testid=order-stalled]").TextContent.ShouldContain("The hub has not answered yet."));
        var reads = _fleet.OrderReads;
        _time.Advance(KitRefresh.Interval * 5);
        _fleet.OrderReads.ShouldBe(reads);
    }

    [Fact]
    public void A_shortfall_is_shown_per_line()
    {
        _fleet.OrderAnswers.Enqueue(ApiResult.Success(FakeFleetClient.Order(ServiceOrderStatuses.Draft, KitStatuses.Partial, [], reserved: 27)));

        var page = RenderOrder();

        page.WaitForAssertion(() => page.Find("[data-testid=order-lines]").TextContent.ShouldContain("3 short"));
        page.FindAll("[aria-label='Status: Kit partly reserved']").ShouldHaveSingleItem();
    }

    [Fact]
    public void The_list_filters_by_the_status_tab_and_marks_the_pressed_one()
    {
        var page = Render<ServiceOrdersPage>(p => p.AddCascadingValue(State(User("engineer", AppRoles.ServiceEngineer))));
        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(1));
        page.Find("[data-testid=orders-new]");

        page.FindAll("[data-testid=orders-tabs] button").Single(b => b.TextContent.Contains("In progress", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => _fleet.OrderCalls[^1].Status.ShouldBe(ServiceOrderStatuses.InProgress));
        page.FindAll("[data-testid=orders-tabs] button[aria-pressed=true]").ShouldHaveSingleItem().TextContent.ShouldContain("In progress");
    }

    [Fact]
    public void A_keeper_reads_the_list_without_the_new_order_button()
    {
        var page = Render<ServiceOrdersPage>(p => p.AddCascadingValue(State(User("keeper", AppRoles.WarehouseKeeper))));

        page.WaitForAssertion(() => page.FindAll("tbody tr.mud-table-row").Count.ShouldBe(1));
        page.FindAll("[data-testid=orders-new]").ShouldBeEmpty();
    }

    [Fact]
    public void The_due_list_offers_an_order_only_where_none_is_open_and_only_to_who_may_create_one()
    {
        var engineer = Render<DueMaintenancePage>(p => p.AddCascadingValue(State(User("engineer", AppRoles.ServiceEngineer))));
        engineer.WaitForAssertion(() => engineer.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));

        engineer.FindAll("[data-testid=due-create]").ShouldHaveSingleItem().GetAttribute("href")
            .ShouldBe("fleet/service-orders/new?machineId=1&planId=1");
        engineer.Markup.ShouldContain("80 h overdue");
        _fleet.DueCalls[0].ShouldBe(((short?)null, FleetSearch.DefaultHorizonDays, (string?)null));

        var planner = Render<DueMaintenancePage>(p => p.AddCascadingValue(State(User("planner", AppRoles.InventoryPlanner))));
        planner.WaitForAssertion(() => planner.FindAll("tbody tr.mud-table-row").Count.ShouldBe(2));
        planner.FindAll("[data-testid=due-create]").ShouldBeEmpty();
    }

    [Fact]
    public void The_form_from_the_due_list_creates_the_planned_service_and_opens_it()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("fleet/service-orders/new?machineId=7&planId=1");

        var page = Render<NewServiceOrderPage>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("At 1,492 h; the parts come from hub KLP."));
        page.Find("[data-testid=new-create]").Click();

        page.WaitForAssertion(() => _fleet.Created.ShouldHaveSingleItem().ShouldBe(new CreateServiceOrderRequest(7, MaintenancePlanId: 1)));
        navigation.Uri.ShouldEndWith("fleet/service-orders/501");
    }

    [Fact]
    public void A_repair_without_parts_is_refused_before_anything_is_sent()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("fleet/service-orders/new?machineId=7");
        var page = Render<NewServiceOrderPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=kind-repair]"));

        page.Find("input[data-testid=kind-repair]").Click();
        page.Find("[data-testid=new-create]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=new-error]").TextContent.ShouldBe("Add at least one part to the repair."));
        _fleet.Created.ShouldBeEmpty();
    }

    [Fact]
    public void An_order_that_already_exists_reads_as_the_servers_sentence()
    {
        _fleet.NextCreate = ApiResult.Failure<ServiceOrderDetails>(new ProblemInfo(
            409, "Fleet.ServiceOrderExists", "Conflict", "The plan already has an open service order for this machine.", "9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("fleet/service-orders/new?machineId=7&planId=1");
        var page = Render<NewServiceOrderPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=new-create]").HasAttribute("disabled").ShouldBeFalse());

        page.Find("[data-testid=new-create]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=new-error]").TextContent
            .ShouldBe("The plan already has an open service order for this machine. Reference: 9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b"));
    }

    private IRenderedComponent<ServiceOrderPage> RenderOrder() =>
        Render<ServiceOrderPage>(p => p.Add(x => x.Id, 501L).AddCascadingValue(State(User("engineer", AppRoles.ServiceEngineer))));
}

/// <summary>The pure rule behind the refresh of the order detail.</summary>
public sealed class KitRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(KitStatuses.Reserving, true)]
    [InlineData(KitStatuses.Reserved, false)]
    [InlineData(KitStatuses.Partial, false)]
    [InlineData(KitStatuses.NotHeld, false)]
    public void An_open_order_is_pending_while_its_kit_is_being_reserved(string kit, bool pending) =>
        KitRefresh.Pending(FakeFleetClient.Order(ServiceOrderStatuses.Draft, kit, []), Now).ShouldBe(pending);

    [Fact]
    public void A_completion_is_pending_until_the_hub_reports_the_issue_or_the_patience_runs_out()
    {
        var completed = FakeFleetClient.Order(ServiceOrderStatuses.Completed, KitStatuses.Issued, []);
        completed = completed with { Order = completed.Order with { CompletedAt = Now.AddSeconds(-5) } };

        KitRefresh.Pending(completed, Now).ShouldBeTrue("nothing issued yet");
        KitRefresh.Pending(completed, Now + KitRefresh.Patience).ShouldBeFalse("a hub without stock issues nothing; the page stops asking");

        var issued = completed with { Lines = [.. completed.Lines.Select(l => l with { QtyIssued = l.QtyRequired, QtyReserved = 0, QtyMissing = 0 })] };
        KitRefresh.Pending(issued, Now).ShouldBeFalse();
    }

    [Fact]
    public void A_cancelled_order_is_never_pending() =>
        KitRefresh.Pending(FakeFleetClient.Order(ServiceOrderStatuses.Cancelled, KitStatuses.Released, []), Now).ShouldBeFalse();
}
