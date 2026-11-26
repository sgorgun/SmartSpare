using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The Fleet read API of stage 6 (STAGE_6_PLAN section 4): machine list, machine card, readings, the due list and the
/// maintenance plans, on the real single host and PostgreSQL 17 as <c>smartspare_api</c>, called with internal tokens.
/// Each test seeds its own model, plans and machines under a unique tag.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class FleetReadApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("fleet.engineer", [AppRoles.ServiceEngineer]);
    private static readonly TestUser Sponsor = TestUser.Create("fleet.sponsor", [AppRoles.Sponsor]);
    private static readonly TestUser NoRole = TestUser.Create("fleet.nobody", []);

    private readonly string _tag = "FL" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private FleetSeed _seed = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _seed = new FleetSeed(database, _tag);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_machine_list_shows_the_most_urgent_plan_and_filters_by_due_status()
    {
        var model = await ModelAsync();
        // Due soon: 470 h, threshold 500, readings 100 days apart at 2 h/day → 30 h, so within the 50 h drafting horizon.
        var dueSoon = await MachineAsync(model, "A", (-100, 270), (-1, 470));
        var ok = await MachineAsync(model, "B", (-100, 10), (-1, 100));
        var overdue = await MachineAsync(model, "C", (-100, 300), (-1, 510));

        var all = await GetAsync<PagedResponse<MachineSummary>>($"/api/v1/fleet/machines?search={_tag}");

        all.TotalCount.ShouldBe(3);
        all.Items.Select(m => (m.Id, m.NextService!.Status)).ShouldBe(
            [(overdue, MaintenanceDueStatuses.Overdue), (dueSoon, MaintenanceDueStatuses.DueSoon), (ok, MaintenanceDueStatuses.Ok)]);
        var first = all.Items[0];
        (first.Vin, first.ModelName, first.BrandCode, first.MachineType, first.WarehouseCode, first.EngineHours, first.MachineStatus)
            .ShouldBe(($"{_tag}C0000", $"Model {_tag}", "JD", "TRACTOR", "KLP", 510m, "active"));
        first.NextService!.DueEngineHours.ShouldBe(500m);
        first.NextService.RemainingHours.ShouldBe(-10m);

        var filtered = await GetAsync<PagedResponse<MachineSummary>>($"/api/v1/fleet/machines?search={_tag}&dueStatus=dueSoon");
        filtered.Items.ShouldHaveSingleItem().Id.ShouldBe(dueSoon);
    }

    [Fact]
    public async Task The_machine_card_shows_every_plan_the_usage_and_the_latest_readings()
    {
        var model = await ModelAsync();
        var machine = await MachineAsync(model, "D", (-100, 270), (-50, 370), (-1, 470));

        var card = await GetAsync<MachineDetails>($"/api/v1/fleet/machines/{machine}");

        card.Machine.Id.ShouldBe(machine);
        (card.OperatingCondition, card.IntervalFactor, card.ManufactureYear).ShouldBe(("NORMAL", 1.0m, (short)2022));
        card.UsagePerDay.ShouldBe(2.02m);   // 200 h over 99 days
        card.Plans.Select(p => (p.PlanName, p.DueEngineHours, p.Status)).ShouldBe(
            [($"Engine {_tag}", 500m, MaintenanceDueStatuses.DueSoon), ($"Transmission {_tag}", 1500m, MaintenanceDueStatuses.Ok)]);
        card.Plans[0].IntervalMonths.ShouldBe((short)12);
        card.RecentReadings.Select(r => r.Hours).ShouldBe([470m, 370m, 270m]);
        card.RecentReadings.ShouldAllBe(r => r.Source == ReadingSources.Import);

        var readings = await GetAsync<PagedResponse<ReadingRow>>($"/api/v1/fleet/machines/{machine}/readings?pageSize=2");
        (readings.TotalCount, readings.Items.Count).ShouldBe((3, 2));
    }

    [Fact]
    public async Task An_unknown_machine_is_not_found()
    {
        using var browser = _host.NewBrowser();
        foreach (var path in new[] { "/api/v1/fleet/machines/999999999", "/api/v1/fleet/machines/999999999/readings" })
        {
            using var response = await browser.GetAsync(path, bearer: Token(Engineer));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            (await Problem.ReadAsync(response)).Code.ShouldBe("Fleet.MachineNotFound");
        }
    }

    [Fact]
    public async Task The_due_list_shows_overdue_and_due_soon_plans_by_due_date()
    {
        var model = await ModelAsync();
        var dueSoon = await MachineAsync(model, "E", (-100, 270), (-1, 470));
        await MachineAsync(model, "F", (-100, 10), (-1, 100));
        var overdue = await MachineAsync(model, "G", (-100, 300), (-1, 510));

        var page = await GetAsync<PagedResponse<DueMaintenanceRow>>("/api/v1/fleet/maintenance/due?pageSize=100");
        var mine = page.Items.Where(r => r.Vin.StartsWith(_tag, StringComparison.Ordinal)).ToList();

        mine.Select(r => (r.MachineId, r.Due.Status)).ShouldBe(
            [(overdue, MaintenanceDueStatuses.Overdue), (dueSoon, MaintenanceDueStatuses.DueSoon)]);
        mine[1].Due.ProjectedDueDate.ShouldBe(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)));   // 30 h at 2 h/day after yesterday's reading
        mine[1].WarehouseCode.ShouldBe("KLP");

        var ok = await GetAsync<PagedResponse<DueMaintenanceRow>>("/api/v1/fleet/maintenance/due?status=ok&pageSize=100");
        ok.Items.Count(r => r.Vin.StartsWith(_tag, StringComparison.Ordinal)).ShouldBe(4, "F twice, E and G their transmission plan");
    }

    [Fact]
    public async Task The_plans_list_their_kits_with_the_snapshot_data()
    {
        var model = await ModelAsync();

        var plans = await GetAsync<IReadOnlyList<MaintenancePlanResponse>>($"/api/v1/fleet/maintenance-plans?modelId={model}");

        plans.Select(p => (p.Name, p.IntervalHours, p.IntervalMonths)).ShouldBe(
            [($"Engine {_tag}", 500, (short?)12), ($"Transmission {_tag}", 1500, (short?)24)]);
        plans[0].Kit.ShouldHaveSingleItem().ShouldBe(new KitPart(_seed.KitPartId, $"{_tag}-OIL", "Kit oil filter", "PCS", 1));
        plans[1].Kit.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/fleet/machines?dueStatus=late", "DueStatus")]
    [InlineData("/api/v1/fleet/machines?pageSize=101", "PageSize")]
    [InlineData("/api/v1/fleet/maintenance/due?horizonDays=400", "HorizonDays")]
    public async Task Invalid_filters_are_validation_problems(string path, string field)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(Engineer));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field);
    }

    [Fact]
    public async Task Every_role_reads_the_fleet_and_a_user_without_a_role_does_not()
    {
        using var browser = _host.NewBrowser();
        using (var sponsor = await browser.GetAsync("/api/v1/fleet/maintenance-plans", bearer: Token(Sponsor)))
        {
            sponsor.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var nobody = await browser.GetAsync("/api/v1/fleet/machines", bearer: Token(NoRole));
        nobody.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }


    private string Token(TestUser user) => _host.Token(user.Principal());

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(Engineer));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private Task<int> ModelAsync() => _seed.ModelAsync();

    private Task<long> MachineAsync(int model, string suffix, params (int DaysAgo, decimal Hours)[] readings) =>
        _seed.MachineAsync(model, suffix, readings);
}
