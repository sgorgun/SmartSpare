using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Recording engine-hour readings (STAGE_6_PLAN 3.1, PR 6f): UC02 and its alternative A1, the odometer rules SS100–SS104,
/// the safe retry of an identical reading, concurrent readings of one machine and the RecordServiceWork policy, on the real
/// single host and PostgreSQL 17 as <c>smartspare_api</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ReadingApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Engineer = TestUser.Create("reading.engineer", [AppRoles.ServiceEngineer]);
    private static readonly TestUser Administrator = TestUser.Create("reading.admin", [AppRoles.Administrator]);

    private readonly string _tag = "RD" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

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
    public async Task UC02_a_reading_that_crosses_the_threshold_drafts_the_service_and_one_below_it_does_not()
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "A", (-20, 400));

        // 440 h: 60 h to the 500 h service, nothing drafted.
        var first = await PostAsync(machine, new RecordReadingRequest(440, Now.AddDays(-10)));
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));
        first.Headers.Location!.ToString().ShouldBe($"/api/v1/fleet/machines/{machine}/readings");
        var stored = (await first.Content.ReadFromJsonAsync<RecordReadingResponse>(Ct))!;
        (stored.Reading.Hours, stored.Reading.Source, stored.Reading.RecordedBy, stored.EngineHours, stored.Replayed)
            .ShouldBe((440m, ReadingSources.Manual, (Guid?)Engineer.Id, 440m, false));
        stored.DraftedOrders.ShouldBeEmpty();

        // 512 h: the 500 h engine service becomes due and is drafted with its kit (no stock at KLP, so not fully reserved).
        var second = await PostAsync(machine, new RecordReadingRequest(512, Now.AddMinutes(-5), ReadingSources.ServiceVisit));
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var drafted = (await second.Content.ReadFromJsonAsync<RecordReadingResponse>(Ct))!;
        drafted.EngineHours.ShouldBe(512m);
        drafted.Reading.Source.ShouldBe(ReadingSources.ServiceVisit);
        var order = drafted.DraftedOrders.ShouldHaveSingleItem();
        order.DueEngineHours.ShouldBe(500m);
        order.Number.ShouldMatch(@"^SO-\d{4}-\d{6}$");
        order.FullyReserved.ShouldBeFalse();

        (await CountAsync("SELECT count(*) FROM fleet.service_order WHERE machine_id = @m", machine)).ShouldBe(1);
    }

    [Theory]
    [InlineData(312, -5, "SS100")]     // A1: odometer rollback below the last reading (440 h)
    [InlineData(900, -1, "SS101")]     // +460 h within about a day
    [InlineData(450, -11, "SS103")]    // older than the last reading
    public async Task Readings_that_break_an_odometer_rule_are_422_with_the_code(int hours, int daysAgo, string code)
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "B", (-10, 440));

        using var response = await PostAsync(machine, new RecordReadingRequest(hours, Now.AddDays(daysAgo)));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(response)).Code.ShouldBe(code);
        (await CountAsync("SELECT count(*) FROM fleet.engine_hours_reading WHERE machine_id = @m", machine)).ShouldBe(1);
    }

    [Fact]
    public async Task A_reading_in_the_future_is_SS104_by_the_server_clock()
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "C", (-10, 440));

        using (var ahead = await PostAsync(machine, new RecordReadingRequest(445, Now.AddMinutes(30))))
        {
            ahead.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(ahead)).Code.ShouldBe("SS104");
        }

        // Within the five-minute clock tolerance a reading is accepted.
        using var tolerated = await PostAsync(machine, new RecordReadingRequest(445, Now.AddMinutes(3)));
        tolerated.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_identical_reading_posted_again_is_a_safe_retry_and_a_different_value_at_that_time_a_conflict()
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "D", (-10, 440));
        var reading = new RecordReadingRequest(450, Now.AddHours(-2).AddTicks(1234));

        using (var created = await PostAsync(machine, reading))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var retried = await PostAsync(machine, reading))
        {
            retried.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = (await retried.Content.ReadFromJsonAsync<RecordReadingResponse>(Ct))!;
            (body.Replayed, body.Reading.Hours, body.DraftedOrders.Count).ShouldBe((true, 450m, 0));
        }

        using (var different = await PostAsync(machine, reading with { Hours = 451 }))
        {
            different.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(different)).Code.ShouldBe("Fleet.ReadingConflict");
        }

        (await CountAsync("SELECT count(*) FROM fleet.engine_hours_reading WHERE machine_id = @m", machine)).ShouldBe(2);
    }

    [Fact]
    public async Task Two_identical_readings_posted_at_once_store_one_row_and_both_succeed()
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "E", (-10, 440));
        var reading = new RecordReadingRequest(450, Now.AddHours(-1));

        var responses = await Task.WhenAll(PostAsync(machine, reading), PostAsync(machine, reading));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Created]);
        (await CountAsync("SELECT count(*) FROM fleet.engine_hours_reading WHERE machine_id = @m", machine)).ShouldBe(2);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Two_conflicting_readings_posted_at_once_store_one_and_refuse_the_other_with_a_code_never_a_500()
    {
        // Last reading 440 h ten days ago. A: 450 h one hour ago; B: 445 h two hours ago. If A is stored first, B is older
        // than the last reading (SS103); if B is first, A adds 5 h within one hour (SS101). Either way one 201 and one 422.
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "F", (-10, 440));

        var responses = await Task.WhenAll(
            PostAsync(machine, new RecordReadingRequest(450, Now.AddHours(-1))),
            PostAsync(machine, new RecordReadingRequest(445, Now.AddHours(-2))));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity]);
        var refused = responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        (await Problem.ReadAsync(refused)).Code.ShouldBeOneOf("SS101", "SS103");
        (await CountAsync("SELECT count(*) FROM fleet.engine_hours_reading WHERE machine_id = @m", machine)).ShouldBe(2);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task An_unknown_machine_is_404_and_invalid_input_400()
    {
        using (var unknown = await PostAsync(999_999_999, new RecordReadingRequest(10)))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await Problem.ReadAsync(unknown)).Code.ShouldBe("Fleet.MachineNotFound");
        }

        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "G", (-10, 440));
        using var invalid = await PostAsync(machine, new RecordReadingRequest(450.25m, Source: "import"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await invalid.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("Hours");
        body.ShouldContain("Source");
    }

    [Theory]
    [InlineData(AppRoles.ServiceEngineer, HttpStatusCode.Created)]
    [InlineData(AppRoles.Administrator, HttpStatusCode.Created)]
    [InlineData(AppRoles.BrandManager, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.WarehouseKeeper, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.InventoryPlanner, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.Sponsor, HttpStatusCode.Forbidden)]
    public async Task Only_service_engineers_and_administrators_record_readings(string role, HttpStatusCode expected)
    {
        var machine = await _seed.MachineAsync(await _seed.ModelAsync(), "H", (-10, 440));
        var user = role == AppRoles.Administrator ? Administrator : TestUser.Create($"reading.{role.ToLowerInvariant()}", [role]);

        using var response = await PostAsync(machine, new RecordReadingRequest(441, Now.AddHours(-1)), user);

        response.StatusCode.ShouldBe(expected);
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<HttpResponseMessage> PostAsync(long machineId, RecordReadingRequest request, TestUser? user = null)
    {
        using var browser = _host.NewBrowser();
        var response = await browser.PostAsync($"/api/v1/fleet/machines/{machineId}/readings", request, bearer: _host.Token((user ?? Engineer).Principal()));
        await response.Content.LoadIntoBufferAsync(Ct);   // the body stays readable after the browser is disposed
        return response;
    }

    private Task<long> CountAsync(string sql, long machineId) => database.ScalarAsync<long>(sql, ("m", machineId));
}
