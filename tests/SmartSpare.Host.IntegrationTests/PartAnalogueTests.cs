using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Part analogues (ADR 0027, PR 7i2a): an interchangeable link works both ways and forms a group; a supersession points
/// one way with at most one successor and no cycle (also when two writers race); every rule holds in the database
/// (SS502); removing or retyping a link needs the user's reason, which the audit records; and the stage 8 demand key
/// resolves the mixed case without doubling or losing demand.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class PartAnalogueTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private const string Links = "/api/v1/inventory/part-analogues";

    private static readonly TestUser Planner = TestUser.Create("analogue.planner", [AppRoles.InventoryPlanner]);

    private readonly string _tag = "A" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_interchangeable_link_is_seen_from_both_parts_and_its_group_is_the_connected_component()
    {
        var (a, b, c) = (await PartAsync("1"), await PartAsync("2"), await PartAsync("3"));
        await LinkAsync(b, a, AnalogueLinkTypes.Interchangeable);
        await LinkAsync(b, c, AnalogueLinkTypes.Interchangeable);

        var fromA = await ViewAsync(a);
        fromA.Links.ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.Relation.ShouldBe(AnalogueRelations.Interchangeable), l => l.Other.Id.ShouldBe(b));
        fromA.Group.Select(p => p.Id).ShouldBe([a, b, c], ignoreOrder: true, "A ≈ B and B ≈ C: one group, transitive by decision 2");
        (await ViewAsync(c)).Group.Select(p => p.Id).ShouldBe([a, b, c], ignoreOrder: true);
    }

    [Fact]
    public async Task A_supersession_points_one_way_with_one_successor_and_never_closes_a_cycle()
    {
        var (oldest, middle, newest, other) = (await PartAsync("4"), await PartAsync("5"), await PartAsync("6"), await PartAsync("7"));
        await LinkAsync(middle, oldest, AnalogueLinkTypes.Supersedes);
        await LinkAsync(newest, middle, AnalogueLinkTypes.Supersedes);

        var view = await ViewAsync(oldest);
        view.Chain.Select(p => p.Id).ShouldBe([oldest, middle, newest], "the chain ends at the current part");
        view.Links.ShouldHaveSingleItem().Relation.ShouldBe(AnalogueRelations.ReplacedBy);

        await RefusedAsync(new PartAnalogueCreate(other, oldest, AnalogueLinkTypes.Supersedes, 1m, null, null), "SS502", "a second successor");
        await RefusedAsync(new PartAnalogueCreate(oldest, newest, AnalogueLinkTypes.Supersedes, 1m, null, null), "SS502", "a cycle");
        await RefusedAsync(new PartAnalogueCreate(middle, oldest, AnalogueLinkTypes.Interchangeable, 1m, null, null), "SS502", "a second link for one pair");
        await RefusedAsync(new PartAnalogueCreate(other, other, AnalogueLinkTypes.Interchangeable, 1m, null, null), "SS502", "a self-link");
    }

    [Fact]
    public async Task The_database_refuses_a_cycle_written_around_the_api_even_when_two_writers_race()
    {
        var (a, b) = (await PartAsync("8"), await PartAsync("9"));
        await using var first = new NpgsqlConnection(database.MigratorConnectionString);
        await using var second = new NpgsqlConnection(database.MigratorConnectionString);
        await first.OpenAsync(Ct);
        await second.OpenAsync(Ct);
        await using var t1 = await first.BeginTransactionAsync(Ct);
        await using var t2 = await second.BeginTransactionAsync(Ct);

        await InsertAsync(first, t1, a, b);
        var racing = InsertAsync(second, t2, b, a);   // waits on the advisory lock until the first commits
        await Task.Delay(300, Ct);
        racing.IsCompleted.ShouldBeFalse("the second writer waits for the first");
        await t1.CommitAsync(Ct);

        var refused = await Should.ThrowAsync<PostgresException>(() => racing);
        refused.SqlState.ShouldBe("SS502", "the waiting writer sees the committed link and the cycle it would close");
    }

    [Fact]
    public async Task Removing_or_retyping_a_link_needs_the_users_reason_and_the_audit_keeps_it()
    {
        var (a, b) = (await PartAsync("10"), await PartAsync("11"));
        var link = await LinkAsync(a, b, AnalogueLinkTypes.Interchangeable);

        using (var noReason = await SendAsync(HttpMethod.Put, $"{Links}/{link.Id}", new PartAnalogueUpdate(AnalogueLinkTypes.Supersedes, a, 1m, link.ValidFrom, null, " "), link.Version))
        {
            noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(noReason)).Code.ShouldBe("Inventory.ReasonRequired");
        }

        using (var retyped = await SendAsync(HttpMethod.Put, $"{Links}/{link.Id}",
                   new PartAnalogueUpdate(AnalogueLinkTypes.Supersedes, a, 1m, link.ValidFrom, null, $"{_tag} catalogue says A replaces B"), link.Version))
        {
            retyped.StatusCode.ShouldBe(HttpStatusCode.OK, await retyped.Content.ReadAsStringAsync(Ct));
            link = (await retyped.Content.ReadFromJsonAsync<PartAnalogueDto>(Ct))!;
            (link.LinkType, link.PartId).ShouldBe((AnalogueLinkTypes.Supersedes, a));
        }

        using (var missing = await SendAsync(HttpMethod.Post, $"{Links}/{link.Id}/remove", new RemoveAnalogueRequest(""), link.Version))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(missing)).Code.ShouldBe("Inventory.ReasonRequired");
        }

        using (var removed = await SendAsync(HttpMethod.Post, $"{Links}/{link.Id}/remove", new RemoveAnalogueRequest($"{_tag} pressure test failed"), link.Version))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync(Ct));
        }

        (await database.ScalarAsync<long>(
                "SELECT count(*) FROM audit.audit_log WHERE table_name = 'catalog.part_analogue' AND reason IN (@retype, @remove)",
                ("retype", $"{_tag} catalogue says A replaces B"), ("remove", $"{_tag} pressure test failed")))
            .ShouldBe(2, "the retype and the removal each carry the user's reason");
    }

    [Fact]
    public async Task The_mixed_case_resolves_to_the_end_of_the_chain_then_pools_by_its_group_and_counts_demand_once()
    {
        // A ≈ B and A → C (C supersedes A): A's demand goes to C, B pools only its own (ADR 0027 decision 5).
        var (a, b, c) = (await PartAsync("12"), await PartAsync("13"), await PartAsync("14"));
        await LinkAsync(a, b, AnalogueLinkTypes.Interchangeable);
        await LinkAsync(c, a, AnalogueLinkTypes.Supersedes);

        var keys = await database.ScalarAsync<string>(
            """
            SELECT string_agg(format('%s:%s:%s', k.part_id, k.current_part_id, k.group_id), ',' ORDER BY k.part_id)
            FROM catalog.v_part_demand_key k WHERE k.part_id IN (@a, @b, @c)
            """,
            ("a", a), ("b", b), ("c", c));
        keys.ShouldBe($"{a}:{c}:{c},{b}:{b}:{Math.Min(a, b)},{c}:{c}:{c}", "A resolves to C; B stays in the group of A and B; C is alone");

        var pooled = await database.ScalarAsync<decimal>(
            """
            WITH demand (part_id, qty) AS (VALUES (@a, 5.0), (@b, 3.0), (@c, 2.0))
            SELECT sum(d.qty * k.factor) FROM demand d JOIN catalog.v_part_demand_key k ON k.part_id = d.part_id
            """,
            ("a", a), ("b", b), ("c", c));
        pooled.ShouldBe(10.0m, "summing over the demand key neither doubles nor loses demand");
    }

    [Fact]
    public async Task An_archived_part_cannot_be_linked()
    {
        var (a, b) = (await PartAsync("15"), await PartAsync("16"));
        await database.ExecuteAsync("UPDATE catalog.part SET is_archived = true, archived_at = now() WHERE id = @id", ("id", b));

        await RefusedAsync(new PartAnalogueCreate(a, b, AnalogueLinkTypes.Interchangeable, 1m, null, null), "SS500", "an archived part");
    }

    [Fact]
    public async Task A_csv_preview_reports_each_pair_error_by_line_column_and_rule_and_writes_nothing()
    {
        var (a, b, c) = (await PartAsync("20"), await PartAsync("21"), await PartAsync("22"));
        await LinkAsync(b, a, AnalogueLinkTypes.Supersedes);
        var csv = Lines(
            "brand_code,oem_part_number,analogue_brand_code,analogue_oem_part_number,link_type,scaling_factor",
            $"JD,{_tag}-22,JD,{_tag}-21,interchangeable,1",
            $"JD,{_tag}-22,JD,{_tag}-20,supersedes,1",
            $"JD,{_tag}-22,JD,{_tag}-21,interchangeable,1",
            $"JD,{_tag}-99,JD,{_tag}-21,interchangeable,1",
            $"JD,{_tag}-20,JD,{_tag}-21,interchangeable,1");

        var result = await ImportAsync(csv, dryRun: true);

        result.Applied.ShouldBeFalse();
        result.Errors.Select(e => (e.Line, e.Column, e.Code)).ShouldBe(
        [
            (3, "analogue_oem_part_number", "RULE"),          // a second successor for -20
            (4, "analogue_oem_part_number", "DUPLICATE"),     // the pair of line 2 again
            (5, "oem_part_number", "UNKNOWN_REF"),            // no such part
            (6, "link_type", "RETYPE"),                       // -20/-21 are linked as a supersession already
        ]);
        (await ViewAsync(c)).Links.ShouldBeEmpty("a preview writes nothing");
    }

    [Fact]
    public async Task A_clean_csv_creates_links_exports_them_and_imports_back_unchanged()
    {
        var (a, b, c) = (await PartAsync("30"), await PartAsync("31"), await PartAsync("32"));
        var csv = Lines(
            "brand_code,oem_part_number,analogue_brand_code,analogue_oem_part_number,link_type,scaling_factor,valid_from,note",
            $"JD,{_tag}-30,JD,{_tag}-31,interchangeable,1,2026-11-01,OEM and aftermarket",
            $"JD,{_tag}-32,JD,{_tag}-30,supersedes,1.2,2026-11-15,");

        var applied = await ImportAsync(csv, dryRun: false);
        (applied.Applied, applied.Created, applied.Errors.Count).ShouldBe((true, 2, 0));
        var view = await ViewAsync(a);
        view.Group.Select(p => p.Id).ShouldBe([a, b], ignoreOrder: true);
        view.Chain.Select(p => p.Id).ShouldBe([a, c]);

        using var browser = _host.NewBrowser();
        using var export = await browser.GetAsync($"{Links}/csv", bearer: _host.Token(Planner.Principal()));
        var exported = await export.Content.ReadAsStringAsync(Ct);
        exported.ShouldContain($"JD,{_tag}-30,JD,{_tag}-31,interchangeable,1,2026-11-01,OEM and aftermarket");
        var mine = Lines([.. exported.TrimStart((char)0xFEFF).ReplaceLineEndings("\n").Split('\n')
            .Where(l => l.StartsWith("brand_code", StringComparison.Ordinal) || l.Contains(_tag, StringComparison.Ordinal))]);
        var again = await ImportAsync(mine, dryRun: false);
        (again.Created, again.Updated, again.Unchanged).ShouldBe((0, 0, 2), "the export imports back without changes");
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private async Task<SmartSpare.ApiContracts.MasterData.CatalogImportResult> ImportAsync(string csv, bool dryRun)
    {
        using var browser = _host.NewBrowser();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "analogues.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Links}/csv?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative)) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Planner.Principal()));
        using var response = await browser.Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SmartSpare.ApiContracts.MasterData.CatalogImportResult>(Ct))!;
    }

    [Fact]
    public async Task Direct_analogues_are_the_direct_interchangeable_links_and_the_current_successor_with_on_hand_minus_reserved()
    {
        // A ≈ B, B ≈ C (C is only an indirect group member of A), A → D → E (E is A's current successor).
        var (a, b, c, d, e) = (await PartAsync("40"), await PartAsync("41"), await PartAsync("42"), await PartAsync("43"), await PartAsync("44"));
        await LinkAsync(a, b, AnalogueLinkTypes.Interchangeable);
        await LinkAsync(b, c, AnalogueLinkTypes.Interchangeable);
        await LinkAsync(d, a, AnalogueLinkTypes.Supersedes);
        await LinkAsync(e, d, AnalogueLinkTypes.Supersedes);
        var klp = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'KLP'");
        await StockAsync(b, klp, 10);
        await database.ExecuteAsync(
            """
            INSERT INTO inventory.stock_reservation (warehouse_id, part_id, quantity, status, expires_at, service_order_line_id)
            VALUES (@w, @p, 4, 'soft', now() + interval '1 day', 990777)
            """,
            ("w", klp), ("p", b));

        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync($"/api/v1/inventory/direct-analogues?partIds={a}&partIds={c}&warehouseId={klp}", bearer: _host.Token(Planner.Principal()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var answer = (await response.Content.ReadFromJsonAsync<List<DirectAnalogues>>(Ct))!;

        answer.Select(x => x.PartId).ShouldBe([a, c], "one entry per part asked, in the order asked");
        answer[0].Analogues.Select(x => (x.Part.Id, x.Kind, x.Available)).ShouldBe(
            [(b, DirectAnalogueKinds.Interchangeable, 6m), (e, DirectAnalogueKinds.Successor, 0m)],
            "B directly (10 on hand - 4 reserved), E as the end of the chain; never C, which is only in the same group");
        answer[1].Analogues.Select(x => x.Part.Id).ShouldBe([b]);
    }

    [Fact]
    public async Task A_successor_or_an_interchangeable_part_cannot_be_archived_but_an_old_superseded_part_can()
    {
        var (old, current, twin) = (await PartAsync("50"), await PartAsync("51"), await PartAsync("52"));
        await LinkAsync(current, old, AnalogueLinkTypes.Supersedes);
        await LinkAsync(current, twin, AnalogueLinkTypes.Interchangeable);

        (await ArchiveReasonsAsync(current)).ShouldBe(["successor of 1 part(s); remove or change the supersession first", "1 interchangeable link(s); remove them first"]);
        (await ArchiveReasonsAsync(twin)).ShouldBe(["1 interchangeable link(s); remove them first"]);
        await database.ExecuteAsync("UPDATE catalog.part SET is_archived = true, archived_at = now() WHERE id = @id", ("id", old));
        (await database.ScalarAsync<bool>("SELECT is_archived FROM catalog.part WHERE id = @id", ("id", old))).ShouldBeTrue("a superseded part may retire");

        var refused = await Should.ThrowAsync<PostgresException>(() =>
            database.ExecuteAsync("UPDATE catalog.part SET is_archived = true, archived_at = now() WHERE id = @id", ("id", current)));
        refused.SqlState.ShouldBe("SS501", "the database refuses it too");
        refused.MessageText.ShouldContain("successor of 1 part(s)");
    }

    private async Task<string?[]> ArchiveReasonsAsync(long part)
    {
        using var browser = _host.NewBrowser();
        using var read = await browser.GetAsync($"/api/v1/inventory/parts?search={_tag}", bearer: _host.Token(Planner.Principal()));
        var version = (await read.Content.ReadFromJsonAsync<SmartSpare.ApiContracts.PagedResponse<PartDto>>(Ct))!.Items.Single(p => p.Id == part).Version;
        using var archive = await SendAsync(HttpMethod.Post, $"/api/v1/inventory/parts/{part}/archive", null, version);
        archive.StatusCode.ShouldBe(HttpStatusCode.Conflict, await archive.Content.ReadAsStringAsync(Ct));
        var body = await archive.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        return [.. body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString())];
    }

    private Task StockAsync(long part, short warehouse, decimal quantity) =>
        database.ExecuteAsync(
            """
            INSERT INTO inventory.warehouse_part (warehouse_id, part_id) VALUES (@w, @p);
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
            VALUES (now(), @w, @p, 'OPENING_BALANCE', @q, 1, 'Analogue test stock');
            """,
            ("w", warehouse), ("p", part), ("q", quantity));

    private Task<long> PartAsync(string suffix) =>
        database.ScalarAsync<long>(
            """
            INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
            SELECT b.id, @oem, 'Analogue test part', c.id, u.id, 1
            FROM catalog.brand b, ref.part_category c, ref.unit_of_measure u WHERE b.code = 'JD' AND c.code = 'OIL_FILTER' AND u.code = 'PCS'
            RETURNING id
            """,
            ("oem", $"{_tag}-{suffix}"));

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long newPart, long oldPart)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO catalog.part_analogue (link_type, part_id, analogue_part_id) VALUES ('supersedes', @n, @o)", connection, transaction);
        command.Parameters.AddWithValue("n", newPart);
        command.Parameters.AddWithValue("o", oldPart);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<PartAnalogueDto> LinkAsync(long part, long analogue, string type)
    {
        using var created = await SendAsync(HttpMethod.Post, Links, new PartAnalogueCreate(part, analogue, type, 1m, null, null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<PartAnalogueDto>(Ct))!;
    }

    private async Task RefusedAsync(PartAnalogueCreate request, string code, string what)
    {
        using var refused = await SendAsync(HttpMethod.Post, Links, request);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, what);
        (await Problem.ReadAsync(refused)).Code.ShouldBe(code, what);
    }

    private async Task<PartAnaloguesView> ViewAsync(long part)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/inventory/parts/{part}/analogues");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PartAnaloguesView>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? version = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Planner.Principal()));
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await browser.Client.SendAsync(request, Ct);
    }
}
