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
