using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The stock API of stage 5 (STAGE_5_PLAN section 3): overview, position, levels, ledger and adjustments, on the real
/// single host and PostgreSQL 17 as <c>smartspare_api</c>, called with internal tokens as the gateway would call a service.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class StockApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Planner = TestUser.Create("stock.planner", [AppRoles.InventoryPlanner]);

    /// <summary>A part-number prefix no other test uses, so searches see only this test's parts.</summary>
    private readonly string _tag = "SQ" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_overview_shows_balance_policy_transit_and_status_per_hub()
    {
        var healthy = await AddPartAsync("001");
        var low = await AddPartAsync("002");
        var empty = await AddPartAsync("003");
        var full = await AddPartAsync("004");
        await StockAsync(healthy, "KLP", onHand: 20, safety: 5, max: 30);
        await StockAsync(low, "KLP", onHand: 3, safety: 5);
        await StockAsync(empty, "KLP", onHand: 0, safety: 1);
        await StockAsync(full, "KLP", onHand: 40, safety: 5, max: 30);

        var page = await GetAsync<PagedResponse<StockRow>>($"/api/v1/inventory/stock?search={_tag}&pageSize=10");

        page.TotalCount.ShouldBe(4);
        page.Items.Select(r => (r.OemPartNumber, r.Status)).ShouldBe(
        [
            ($"{_tag}-001", StockStatuses.Ok), ($"{_tag}-002", StockStatuses.BelowSafety),
            ($"{_tag}-003", StockStatuses.Out), ($"{_tag}-004", StockStatuses.AboveMax),
        ]);
        var row = page.Items[0];
        (row.WarehouseCode, row.OnHand, row.Reserved, row.Available, row.SafetyStock, row.MaxStock, row.InTransit)
            .ShouldBe(("KLP", 20m, 0m, 20m, 5m, (decimal?)30m, 0m));
        row.LastMovementAt.ShouldNotBeNull();

        var below = await GetAsync<PagedResponse<StockRow>>($"/api/v1/inventory/stock?search={_tag}&status={StockStatuses.BelowSafety}");
        below.Items.ShouldHaveSingleItem().PartId.ShouldBe(low);

        var klp = await WarehouseIdAsync("KLP");
        var other = await GetAsync<PagedResponse<StockRow>>($"/api/v1/inventory/stock?search={_tag}&warehouseId={klp + 1}");
        other.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Dispatched_transfers_count_as_in_transit_at_the_recipient()
    {
        var part = await AddPartAsync("010");
        await StockAsync(part, "KLP", onHand: 20, safety: 0);
        await StockAsync(part, "PNV", onHand: 1, safety: 0);
        await database.ExecuteAsync(
            """
            WITH o AS (
                INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id)
                SELECT (SELECT id FROM inventory.warehouse WHERE code = 'KLP'), (SELECT id FROM inventory.warehouse WHERE code = 'PNV')
                RETURNING id)
            INSERT INTO inventory.transfer_order_line (transfer_order_id, part_id, quantity) SELECT id, @part, 4 FROM o;
            UPDATE inventory.transfer_order SET status = 'approved', approved_at = now()
             WHERE id = (SELECT transfer_order_id FROM inventory.transfer_order_line WHERE part_id = @part);
            UPDATE inventory.transfer_order SET status = 'dispatched', dispatched_at = now()
             WHERE id = (SELECT transfer_order_id FROM inventory.transfer_order_line WHERE part_id = @part);
            """,
            ("part", part));

        var page = await GetAsync<PagedResponse<StockRow>>($"/api/v1/inventory/stock?search={_tag}-010");

        page.Items.Single(r => r.WarehouseCode == "PNV").InTransit.ShouldBe(4);
        page.Items.Single(r => r.WarehouseCode == "KLP").InTransit.ShouldBe(0);
    }

    [Fact]
    public async Task A_position_has_its_version_reservations_and_latest_movements()
    {
        var part = await AddPartAsync("020");
        await StockAsync(part, "KLP", onHand: 12, safety: 2);
        var klp = await WarehouseIdAsync("KLP");

        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/inventory/stock/{klp}/{part}", bearer: Token(Planner));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var position = (await response.Content.ReadFromJsonAsync<StockPositionDetails>(Ct))!;
        response.Headers.ETag!.Tag.ShouldBe($"\"{position.Version}\"");
        position.Stock.OnHand.ShouldBe(12);
        position.Reservations.ShouldBeEmpty();
        position.RecentMovements.ShouldHaveSingleItem().MovementType.ShouldBe("OPENING_BALANCE");
        position.RecentMovements[0].Quantity.ShouldBe(12);
    }

    [Fact]
    public async Task An_unstocked_position_is_a_404_with_a_stable_code()
    {
        var part = await AddPartAsync("030");
        var klp = await WarehouseIdAsync("KLP");

        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/inventory/stock/{klp}/{part}", bearer: Token(Planner));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Problem.ReadAsync(response)).Code.ShouldBe("Inventory.StockPositionNotFound");
    }

    [Fact]
    public async Task Stock_levels_change_only_against_the_version_that_was_read()
    {
        var part = await AddPartAsync("040");
        await StockAsync(part, "KLP", onHand: 12, safety: 2);
        var klp = await WarehouseIdAsync("KLP");
        var path = $"/api/v1/inventory/stock/{klp}/{part}";
        var version = (await GetAsync<StockPositionDetails>(path)).Version;

        using (var changed = await PutLevelsAsync(path + "/levels", new ChangeStockLevelsRequest(6, 30), $"\"{version}\""))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var row = (await changed.Content.ReadFromJsonAsync<StockRow>(Ct))!;
            (row.SafetyStock, row.MaxStock).ShouldBe((6m, (decimal?)30m));
        }

        using (var stale = await PutLevelsAsync(path + "/levels", new ChangeStockLevelsRequest(7, null), $"\"{version}\""))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(stale)).Code.ShouldBe("Database.ConcurrencyConflict");
        }

        using (var missing = await PutLevelsAsync(path + "/levels", new ChangeStockLevelsRequest(7, null), ifMatch: null))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(missing)).Code.ShouldBe("Inventory.VersionRequired");
        }

        using (var invalid = await PutLevelsAsync(path + "/levels", new ChangeStockLevelsRequest(7, 6), $"\"{version}\""))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await GetAsync<StockPositionDetails>(path)).Stock.SafetyStock.ShouldBe(6);
    }

    [Fact]
    public async Task Keepers_adjust_their_own_hub_and_the_ledger_refuses_negative_stock()
    {
        var part = await AddPartAsync("050");
        await StockAsync(part, "KLP", onHand: 3, safety: 0);
        var klp = await WarehouseIdAsync("KLP");
        var keeper = TestUser.Create("klp.keeper", [AppRoles.WarehouseKeeper], warehouse: klp);
        var otherKeeper = TestUser.Create("pnv.keeper", [AppRoles.WarehouseKeeper], warehouse: await WarehouseIdAsync("PNV"));
        using var browser = _host.NewBrowser();

        using (var surplus = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, 2, 5, "Stock count surplus"), bearer: Token(keeper)))
        {
            surplus.StatusCode.ShouldBe(HttpStatusCode.Created, await surplus.Content.ReadAsStringAsync(Ct));
            var body = (await surplus.Content.ReadFromJsonAsync<StockAdjustmentResponse>(Ct))!;
            body.MovementId.ShouldBeGreaterThan(0);
            body.Stock.OnHand.ShouldBe(5);
        }

        using (var shortage = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, -6, 3, "Lost"), bearer: Token(keeper)))
        {
            shortage.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(shortage)).Code.ShouldBe("SS200");
        }

        using (var foreign = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, -1, 3, "Wrong hub"), bearer: Token(otherKeeper)))
        {
            foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Problem.ReadAsync(foreign)).Code.ShouldBe("SS302");
        }

        using (var noNote = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, 1, 1, ""), bearer: Token(keeper)))
        {
            noNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var wrongDirection = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, 1, 2, null), bearer: Token(keeper)))
        {
            wrongDirection.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "DAMAGED allows shortages only");
            (await Problem.ReadAsync(wrongDirection)).Code.ShouldBe("Inventory.ReasonDirection");
        }

        using (var planner = await browser.PostAsync("/api/v1/inventory/stock-adjustments",
                   new StockAdjustmentRequest(klp, part, 1, 5, "Not a keeper"), bearer: Token(Planner)))
        {
            planner.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var ledger = await GetAsync<PagedResponse<MovementRow>>($"/api/v1/inventory/movements?partId={part}");
        // Only the accepted surplus was posted; the refused shortage and the foreign keeper left no movement.
        ledger.Items.Select(m => (m.MovementType, m.Quantity)).ShouldBe([("ADJUSTMENT_IN", 2m), ("OPENING_BALANCE", 3m)], ignoreOrder: true);
        ledger.Items.Single(m => m.MovementType == "ADJUSTMENT_IN").Note.ShouldBe("Stock count surplus");
    }

    [Theory]
    [InlineData("/api/v1/inventory/stock?status=low", "Status")]
    [InlineData("/api/v1/inventory/stock?pageSize=101", "PageSize")]
    [InlineData("/api/v1/inventory/movements?from=2026-11-02T00:00:00Z&to=2026-11-01T00:00:00Z", "To")]
    public async Task Invalid_queries_are_validation_problems(string path, string field)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(Planner));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field);
    }

    [Fact]
    public async Task Other_services_read_the_warehouses_through_the_internal_endpoint_with_the_callers_token()
    {
        using var browser = _host.NewBrowser();
        var queries = new HttpWarehouseQueries(new HttpClient(new BearerHandler(Token(Planner), _host.Server.CreateHandler()))
        {
            BaseAddress = _host.Server.BaseAddress,
        });

        var warehouses = await queries.GetAllAsync(Ct);

        warehouses.Count.ShouldBe(7);
        warehouses.Select(w => w.Code).ShouldBe(warehouses.Select(w => w.Code).Order(StringComparer.Ordinal));
        (await queries.ExistsAsync(warehouses[0].Id, Ct)).ShouldBeTrue();
        (await queries.ExistsAsync(999, Ct)).ShouldBeFalse();

        // Refused without a user: 401 from a service (bearer only), a redirect to sign-in from the single host's cookie scheme.
        using var anonymous = await browser.GetAsync(InventoryInternalApi.Warehouses);
        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    private string Token(TestUser user) => _host.Token(user.Principal());

    private async Task<HttpResponseMessage> PutLevelsAsync(string path, ChangeStockLevelsRequest body, string? ifMatch)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Planner));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await browser.Client.SendAsync(request, Ct);
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(path, bearer: Token(Planner));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private Task<short> WarehouseIdAsync(string code) =>
        database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = @code", ("code", code));

    private Task<long> AddPartAsync(string suffix) =>
        database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Stock test part', c.id, u.id, 18.40
            FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """,
            ("oem", $"{_tag}-{suffix}"));

    /// <summary>A stocking policy and an opening balance through the ledger; the balance follows by trigger.</summary>
    private async Task StockAsync(long partId, string warehouseCode, decimal onHand, decimal safety, decimal? max = null)
    {
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id, safety_stock_qty, max_stock_qty)
            SELECT w.id, @part, @safety, @max FROM inventory.warehouse w WHERE w.code = @warehouse;
            """,
            ("part", partId), ("warehouse", warehouseCode), ("safety", safety), ("max", (object?)max ?? DBNull.Value));
        if (onHand > 0)
        {
            await database.ExecuteAsync(
                """
                INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
                SELECT now(), w.id, @part, 'OPENING_BALANCE', @quantity, 18.40, 'Test stock'
                FROM inventory.warehouse w WHERE w.code = @warehouse;
                """,
                ("part", partId), ("warehouse", warehouseCode), ("quantity", onHand));
        }
    }

    private sealed class BearerHandler(string token, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
