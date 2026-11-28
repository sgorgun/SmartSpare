using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The transfer API of stage 5 (STAGE_5_PLAN sections 2.1, 3, 4): the full flow propose → approve → dispatch → receive,
/// the role × action matrix with brand scope and home hubs, and the SQL errors mapped to ProblemDetails.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class TransferApiTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private readonly string _tag = "TQ" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private short _klp;
    private short _pnv;
    private short _jd;
    private short _claas;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _klp = await WarehouseIdAsync("KLP");
        _pnv = await WarehouseIdAsync("PNV");
        // BrandIdsAsync orders by id, so each brand is resolved on its own.
        _jd = (await database.BrandIdsAsync("JD")).Single();
        _claas = (await database.BrandIdsAsync("CLAAS")).Single();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static TestUser Planner => TestUser.Create("tr.planner", [AppRoles.InventoryPlanner]);

    private TestUser JdManager => TestUser.Create("tr.jd.manager", [AppRoles.BrandManager], brands: [_jd]);

    private TestUser DonorKeeper => TestUser.Create("tr.klp.keeper", [AppRoles.WarehouseKeeper], warehouse: _klp);

    private TestUser RecipientKeeper => TestUser.Create("tr.pnv.keeper", [AppRoles.WarehouseKeeper], warehouse: _pnv);

    [Fact]
    public async Task A_transfer_goes_from_proposal_to_receipt_and_moves_the_stock()
    {
        var part = await StockedPartAsync("001", "JD", onHand: 20, safety: 5);

        var proposed = await CreateAsync(Planner, part, 11);
        proposed.Order.Status.ShouldBe(TransferStatuses.Proposed);
        proposed.Order.Number.ShouldMatch(@"^TR-\d{4}-\d{6}$");
        proposed.Lines.ShouldHaveSingleItem().DonorAvailable.ShouldBe(20);
        proposed.AllowedActions.ShouldBe([TransferActions.Approve, TransferActions.Cancel]);
        (await DetailsAsync(DonorKeeper, proposed.Order.Id)).AllowedActions.ShouldBeEmpty("a keeper neither approves nor cancels");

        var approved = await StepAsync(Planner, proposed.Order.Id, "approve");
        approved.Order.Status.ShouldBe(TransferStatuses.Approved);
        approved.Lines[0].DonorAvailable.ShouldBe(9, "11 are firmly reserved at the donor");
        (await DetailsAsync(DonorKeeper, proposed.Order.Id)).AllowedActions.ShouldBe([TransferActions.Dispatch]);
        (await DetailsAsync(RecipientKeeper, proposed.Order.Id)).AllowedActions.ShouldBeEmpty();

        var dispatched = await StepAsync(DonorKeeper, proposed.Order.Id, "dispatch");
        dispatched.Order.Status.ShouldBe(TransferStatuses.Dispatched);
        (await DetailsAsync(RecipientKeeper, proposed.Order.Id)).AllowedActions.ShouldBe([TransferActions.Receive]);

        var received = await StepAsync(RecipientKeeper, proposed.Order.Id, "receive");
        received.Order.Status.ShouldBe(TransferStatuses.Received);
        received.Timeline.Select(s => s.Step).ShouldBe(["requested", "approved", "dispatched", "received"]);
        received.AllowedActions.ShouldBeEmpty();

        (await OnHandAsync(part, _klp)).ShouldBe(9);
        (await OnHandAsync(part, _pnv)).ShouldBe(11);
    }

    [Fact]
    public async Task Transfer_steps_write_no_outbox_rows_until_a_consumer_exists()
    {
        // TransferApproved and TransferCancelled are written from stage 8 with their first consumer (#14); until then a
        // step changes the data only (STAGE_5_PLAN section 4.1). Stage 8 replaces this test with the one-row assertion.
        var part = await StockedPartAsync("002", "JD", onHand: 20, safety: 0);
        var approved = await CreateAsync(Planner, part, 4);
        await StepAsync(Planner, approved.Order.Id, "approve");
        var cancelled = await CreateAsync(Planner, part, 1);

        using (var response = await PostAsync(Planner, $"/api/v1/inventory/transfers/{cancelled.Order.Id}/cancel", new CancelTransferRequest("Not needed")))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        (await OutboxCountAsync(approved.Order.Id, "TransferApproved")).ShouldBe(0);
        (await OutboxCountAsync(cancelled.Order.Id, "TransferCancelled")).ShouldBe(0);
        (await DetailsAsync(Planner, cancelled.Order.Id)).CancelReason.ShouldBe("Not needed");
    }

    [Fact]
    public async Task A_donor_never_gives_its_safety_stock_unless_the_transfer_is_urgent()
    {
        var part = await StockedPartAsync("003", "JD", onHand: 10, safety: 5);
        var normal = await CreateAsync(Planner, part, 6);

        using (var refused = await PostAsync(Planner, $"/api/v1/inventory/transfers/{normal.Order.Id}/approve"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(refused)).Code.ShouldBe("SS201");
        }

        (await DetailsAsync(Planner, normal.Order.Id)).Order.Status.ShouldBe(TransferStatuses.Proposed);

        var urgent = await CreateAsync(Planner, part, 6, isUrgent: true);
        (await StepAsync(Planner, urgent.Order.Id, "approve")).Order.Status.ShouldBe(TransferStatuses.Approved);

        var tooMuch = await CreateAsync(Planner, part, 5, isUrgent: true);
        using var none = await PostAsync(Planner, $"/api/v1/inventory/transfers/{tooMuch.Order.Id}/approve");
        none.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem.ReadAsync(none)).Code.ShouldBe("SS200");
    }

    [Fact]
    public async Task A_brand_manager_decides_only_on_transfers_of_their_brands()
    {
        var jdPart = await StockedPartAsync("004", "JD", onHand: 20, safety: 0);
        var claasPart = await StockedPartAsync("005", "CLAAS", onHand: 20, safety: 0);
        var own = await CreateAsync(Planner, jdPart, 1);
        var mixed = await CreateAsync(Planner, [(jdPart, 1m), (claasPart, 1m)]);

        (await DetailsAsync(JdManager, mixed.Order.Id)).AllowedActions.ShouldBeEmpty();
        using (var outOfScope = await PostAsync(JdManager, $"/api/v1/inventory/transfers/{mixed.Order.Id}/approve"))
        {
            outOfScope.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Problem.ReadAsync(outOfScope)).Code.ShouldBe("Inventory.OutOfBrandScope");
        }

        using (var cancelOutOfScope = await PostAsync(JdManager, $"/api/v1/inventory/transfers/{mixed.Order.Id}/cancel", new CancelTransferRequest("No")))
        {
            cancelOutOfScope.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await DetailsAsync(JdManager, own.Order.Id)).AllowedActions.ShouldBe([TransferActions.Approve, TransferActions.Cancel]);
        (await StepAsync(JdManager, own.Order.Id, "approve")).Order.Status.ShouldBe(TransferStatuses.Approved);
    }

    [Fact]
    public async Task Keepers_move_only_their_own_hubs_stock()
    {
        var part = await StockedPartAsync("006", "JD", onHand: 20, safety: 0);
        var order = await CreateAsync(Planner, part, 2);
        await StepAsync(Planner, order.Order.Id, "approve");

        using (var wrongHub = await PostAsync(RecipientKeeper, $"/api/v1/inventory/transfers/{order.Order.Id}/dispatch"))
        {
            wrongHub.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await Problem.ReadAsync(wrongHub)).Code.ShouldBe("SS302");
        }

        using (var tooEarly = await PostAsync(RecipientKeeper, $"/api/v1/inventory/transfers/{order.Order.Id}/receive"))
        {
            tooEarly.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Problem.ReadAsync(tooEarly)).Code.ShouldBe("SS300");
        }

        using var planner = await PostAsync(Planner, $"/api/v1/inventory/transfers/{order.Order.Id}/dispatch");
        planner.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "planners do not move stock");
    }

    [Theory]
    [InlineData(AppRoles.Sponsor)]
    [InlineData(AppRoles.ServiceEngineer)]
    [InlineData(AppRoles.WarehouseKeeper)]
    public async Task Roles_outside_planning_cannot_propose(string role)
    {
        var part = await StockedPartAsync($"R{role[..2]}", "JD", onHand: 1, safety: 0);
        using var response = await PostAsync(
            TestUser.Create($"tr.{role.ToLowerInvariant()}", [role]), "/api/v1/inventory/transfers",
            new CreateTransferRequest(_klp, _pnv, false, [new CreateTransferLine(part, 1)]));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invalid_proposals_are_refused_with_their_reason()
    {
        var part = await StockedPartAsync("007", "JD", onHand: 1, safety: 0);

        using (var same = await PostAsync(Planner, "/api/v1/inventory/transfers", new CreateTransferRequest(_klp, _klp, false, [new CreateTransferLine(part, 1)])))
        {
            same.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var unknown = await PostAsync(Planner, "/api/v1/inventory/transfers", new CreateTransferRequest(_klp, _pnv, false, [new CreateTransferLine(999_999_999, 1)])))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(unknown)).Code.ShouldBe("Inventory.UnknownPart");
        }

        using (var missing = await GetRawAsync(Planner, "/api/v1/inventory/transfers/999999999"))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await Problem.ReadAsync(missing)).Code.ShouldBe("Inventory.TransferNotFound");
        }
    }

    [Fact]
    public async Task The_list_filters_by_status_and_hub()
    {
        var part = await StockedPartAsync("008", "JD", onHand: 5, safety: 0);
        var order = await CreateAsync(Planner, part, 1);

        using var response = await GetRawAsync(Planner, $"/api/v1/inventory/transfers?status=proposed&warehouseId={_pnv}&pageSize=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<PagedResponse<TransferSummary>>(Ct))!;
        page.Items.ShouldContain(t => t.Id == order.Order.Id);
        page.Items.ShouldAllBe(t => t.Status == TransferStatuses.Proposed);

        using var invalid = await GetRawAsync(Planner, "/api/v1/inventory/transfers?status=open");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<TransferDetails> CreateAsync(TestUser user, long part, decimal quantity, bool isUrgent = false) =>
        CreateAsync(user, [(part, quantity)], isUrgent);

    private async Task<TransferDetails> CreateAsync(TestUser user, (long Part, decimal Quantity)[] lines, bool isUrgent = false)
    {
        using var response = await PostAsync(user, "/api/v1/inventory/transfers",
            new CreateTransferRequest(_klp, _pnv, isUrgent, [.. lines.Select(l => new CreateTransferLine(l.Part, l.Quantity))]));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var created = (await response.Content.ReadFromJsonAsync<TransferDetails>(Ct))!;
        response.Headers.Location!.ToString().ShouldEndWith("/api/v1/inventory/transfers/" + created.Order.Id);
        return created;
    }

    private async Task<TransferDetails> StepAsync(TestUser user, long id, string step)
    {
        using var response = await PostAsync(user, $"/api/v1/inventory/transfers/{id}/{step}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TransferDetails>(Ct))!;
    }

    private async Task<TransferDetails> DetailsAsync(TestUser user, long id)
    {
        using var response = await GetRawAsync(user, $"/api/v1/inventory/transfers/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TransferDetails>(Ct))!;
    }

    private async Task<HttpResponseMessage> PostAsync(TestUser user, string path, object? body = null)
    {
        using var browser = _host.NewBrowser();
        return await browser.PostAsync(path, body, bearer: _host.Token(user.Principal()));
    }

    private async Task<HttpResponseMessage> GetRawAsync(TestUser user, string path)
    {
        using var browser = _host.NewBrowser();
        return await browser.GetAsync(path, bearer: _host.Token(user.Principal()));
    }

    private Task<short> WarehouseIdAsync(string code) =>
        database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = @code", ("code", code));

    private Task<decimal> OnHandAsync(long part, short warehouse) =>
        database.ScalarAsync<decimal>(
            "SELECT on_hand_qty FROM inventory.stock_balance WHERE part_id = @part AND warehouse_id = @warehouse",
            ("part", part), ("warehouse", warehouse));

    private Task<long> OutboxCountAsync(long transferId, string type) =>
        database.ScalarAsync<long>(
            "SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.' || @type || ',%' AND (payload ->> 'transferOrderId')::bigint = @id",
            ("type", type), ("id", transferId));

    /// <summary>A part of <paramref name="brand"/> stocked at KLP with an opening balance; PNV stocks it empty.</summary>
    private async Task<long> StockedPartAsync(string suffix, string brand, decimal onHand, decimal safety)
    {
        var part = await database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Transfer test part', c.id, u.id, 18.40
            FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u
            WHERE b.code = @brand AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """,
            ("oem", $"{_tag}-{suffix}"), ("brand", brand));
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id, safety_stock_qty)
            SELECT w.id, @part, CASE WHEN w.code = 'KLP' THEN @safety ELSE 0 END FROM inventory.warehouse w WHERE w.code IN ('KLP', 'PNV');
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            SELECT now() - interval '1 minute', w.id, @part, 'OPENING_BALANCE', @quantity, 18.40, 'Test stock'
            FROM inventory.warehouse w WHERE w.code = 'KLP';
            """,
            ("part", part), ("safety", safety), ("quantity", onHand));
        return part;
    }
}
