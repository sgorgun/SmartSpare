using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// Rights from responsibility assignments in the single host (ADR 0026 decision 3, STAGE_7_PLAN 1.3): an administrator
/// assigns a keeper on the warehouse or a brand manager on the brand (Inventory), <c>ResponsibilitiesChanged</c> goes
/// through Inventory's outbox to Identity, whose projection gives the claims, and the account's stamp rotates. A
/// redelivered or older event changes nothing; only administrators assign rights; reads stay open.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ResponsibilityFlowTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Admin = TestUser.Create("resp.admin", [AppRoles.Administrator]);

    private readonly string _tag = "rf" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    private HostFactory _host = null!;
    private short _klp;
    private short _pnv;
    private short _claas;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _klp = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'KLP'");
        _pnv = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'PNV'");
        _claas = await database.ScalarAsync<short>("SELECT id FROM catalog.brand WHERE code = 'CLAAS'");
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_keeper_assignment_gives_hub_access_through_the_event_and_revoking_it_takes_it_away()
    {
        var keeper = await database.CreateUserAsync($"{_tag}.keeper", [AppRoles.WarehouseKeeper], []);
        var stamp = await StampAsync(keeper);
        (await HubsAsync($"{_tag}.keeper")).ShouldBeEmpty("no assignment, no hub");

        var assigned = await ReplaceAsync("warehouses", _klp, [new ResponsibleEntry(keeper, "KEEPER")]);
        assigned.ShouldHaveSingleItem().GrantsScope.ShouldBeTrue();
        assigned[0].EmployeeName.ShouldBe("Test User", "the name comes from the employee directory");

        await EventuallyAsync(async () => (await HubsAsync($"{_tag}.keeper")).SequenceEqual([_klp.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        (await StampAsync(keeper)).ShouldNotBe(stamp, "the session picks up the new hub at its next check");
        (await database.ScalarAsync<long>("SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%ResponsibilitiesChanged%'"))
            .ShouldBeGreaterThan(0, "the change went through Inventory's outbox");

        await ReplaceAsync("warehouses", _klp, []);
        await EventuallyAsync(async () => (await HubsAsync($"{_tag}.keeper")).Count == 0);
    }

    [Fact]
    public async Task A_brand_manager_assignment_gives_approval_scope_only_with_the_role()
    {
        var manager = await database.CreateUserAsync($"{_tag}.manager", [AppRoles.BrandManager], []);
        var planner = await database.CreateUserAsync($"{_tag}.planner", [AppRoles.InventoryPlanner], []);

        await ReplaceAsync("brands", _claas, [new ResponsibleEntry(manager, "BRAND_MANAGER"), new ResponsibleEntry(planner, "BRAND_MANAGER")]);

        await EventuallyAsync(async () => (await ClaimsAsync($"{_tag}.manager", AppClaimTypes.Brand)).Contains(_claas.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        (await ClaimsAsync($"{_tag}.planner", AppClaimTypes.Brand)).ShouldBeEmpty("a brand gives scope only with the BRAND_MANAGER role");
    }

    [Fact]
    public async Task Assignments_without_rights_publish_nothing_and_change_no_claims()
    {
        var keeper = await database.CreateUserAsync($"{_tag}.hubboss", [AppRoles.WarehouseKeeper], []);
        var before = await database.ScalarAsync<long>("SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%ResponsibilitiesChanged%'");
        var stamp = await StampAsync(keeper);

        await ReplaceAsync("warehouses", _pnv, [new ResponsibleEntry(keeper, "HUB_MANAGER")]);

        (await database.ScalarAsync<long>("SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%ResponsibilitiesChanged%'")).ShouldBe(before);
        (await StampAsync(keeper)).ShouldBe(stamp);
        await ReplaceAsync("warehouses", _pnv, []);
    }

    [Fact]
    public async Task A_redelivered_or_older_event_changes_nothing()
    {
        var keeper = await database.CreateUserAsync($"{_tag}.order", [AppRoles.WarehouseKeeper], []);
        var hub = await database.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'SIA'");
        var current = new ResponsibilitiesChanged(ResponsibilitiesChanged.Warehouse, hub, [new ResponsibleAssignment(keeper, "KEEPER")], Admin.Id);
        (await DeliverAsync(current, current.Id)).ShouldBeTrue();
        (await HubsAsync($"{_tag}.order")).ShouldContain(hub.ToString(System.Globalization.CultureInfo.InvariantCulture));

        (await DeliverAsync(current, current.Id)).ShouldBeFalse("the inbox skips a redelivered message");

        var older = new ResponsibilitiesChanged(ResponsibilitiesChanged.Warehouse, hub, [], Admin.Id) { OccurredAt = current.OccurredAt.AddMinutes(-5) };
        (await DeliverAsync(older, older.Id)).ShouldBeTrue();
        (await HubsAsync($"{_tag}.order")).ShouldContain(hub.ToString(System.Globalization.CultureInfo.InvariantCulture), "an older state never overwrites a newer one");

        var newer = new ResponsibilitiesChanged(ResponsibilitiesChanged.Warehouse, hub, [], Admin.Id) { OccurredAt = current.OccurredAt.AddMinutes(5) };
        (await DeliverAsync(newer, newer.Id)).ShouldBeTrue();
        (await HubsAsync($"{_tag}.order")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_administrators_assign_rights_and_every_role_reads_the_responsibles()
    {
        var keeper = TestUser.Create("resp.keeper", [AppRoles.WarehouseKeeper], warehouse: _klp);
        using (var refused = await SendAsync(keeper, HttpMethod.Put, $"/api/v1/inventory/warehouses/{_klp}/responsibles", new ResponsiblesRequest([])))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var read = await SendAsync(keeper, HttpMethod.Get, $"/api/v1/inventory/warehouses/{_pnv}/responsibles"))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.OK, "reads are never scoped by hub");
        }

        using (var roles = await SendAsync(keeper, HttpMethod.Get, "/api/v1/inventory/assignment-roles?kind=warehouse"))
        {
            (await roles.Content.ReadFromJsonAsync<List<AssignmentRoleDto>>(Ct))!.Select(r => r.Code).ShouldBe(["KEEPER", "SERVICE_BASE", "HUB_MANAGER"], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Unknown_roles_and_employees_are_refused_with_their_reason()
    {
        var employee = await database.CreateUserAsync($"{_tag}.valid", [AppRoles.WarehouseKeeper], []);

        using (var role = await SendAsync(Admin, HttpMethod.Put, $"/api/v1/inventory/warehouses/{_klp}/responsibles",
                   new ResponsiblesRequest([new ResponsibleEntry(employee, "BRAND_MANAGER")])))
        {
            role.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Problem.ReadAsync(role)).Code.ShouldBe("Inventory.UnknownAssignmentRole");
        }

        using (var unknown = await SendAsync(Admin, HttpMethod.Put, $"/api/v1/inventory/warehouses/{_klp}/responsibles",
                   new ResponsiblesRequest([new ResponsibleEntry(Guid.NewGuid(), "KEEPER")])))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(unknown)).Code.ShouldBe("Inventory.UnknownEmployee");
        }

        using var missing = await SendAsync(Admin, HttpMethod.Put, "/api/v1/inventory/warehouses/999/responsibles", new ResponsiblesRequest([]));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<List<ResponsibleDto>> ReplaceAsync(string segment, short id, IReadOnlyList<ResponsibleEntry> entries)
    {
        using var response = await SendAsync(Admin, HttpMethod.Put, $"/api/v1/inventory/{segment}/{id}/responsibles", new ResponsiblesRequest(entries));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<List<ResponsibleDto>>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(user.Principal()));
        return await browser.Client.SendAsync(request, Ct);
    }

    /// <summary>The claims a fresh sign-in gets, through the real Identity module.</summary>
    private async Task<List<string>> ClaimsAsync(string userName, string claimType)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<ICredentialVerifier>()
            .VerifyAsync(userName, IdentityDatabaseFixture.Password, IPAddress.Loopback, Ct);
        session.IsSuccess.ShouldBeTrue();
        return [.. session.Value.Claims.Where(c => c.Type == claimType).Select(c => c.Value)];
    }

    private Task<List<string>> HubsAsync(string userName) => ClaimsAsync(userName, AppClaimTypes.Warehouse);

    private Task<Guid> StampAsync(Guid id) =>
        database.ScalarAsync<Guid>("SELECT security_stamp FROM iam.app_user WHERE id = @id", ("id", id));

    private async Task<bool> DeliverAsync(ResponsibilitiesChanged changed, Guid messageId)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetServices<IIntegrationEventConsumer<ResponsibilitiesChanged>>().ShouldHaveSingleItem();
        return await IntegrationEventDelivery.DeliverAsync(
            scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, changed, messageId, Ct);
    }

    /// <summary>The single host delivers through the outbox dispatcher's polling interval; waits up to 30 s.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        (await condition()).ShouldBeTrue("the event flow did not complete within 30 s");
    }
}
