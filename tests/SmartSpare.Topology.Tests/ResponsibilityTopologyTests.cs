using System.Net;
using System.Net.Http.Json;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.ApiContracts.Inventory;

namespace SmartSpare.Topology.Tests;

/// <summary>
/// Rights from responsibility assignments in the services topology (ADR 0026 decision 3): an administrator assigns a
/// keeper on a warehouse through the gateway; the Inventory service names the employee through the Identity service's
/// <c>/internal/v1</c> and publishes <c>ResponsibilitiesChanged</c> over RabbitMQ; the Identity service projects it, so
/// the keeper's next sign-in carries the hub. Each service on its own database login.
/// </summary>
[Trait("Category", "Topology")]
[Collection(TopologyGroup.Name)]
public sealed class ResponsibilityTopologyTests(TopologyFixture topology)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_keeper_assigned_in_the_inventory_service_gets_the_hub_from_the_identity_service_over_rabbitmq()
    {
        var keeper = await topology.CreateUserAsync("topo.resp.keeper", [AppRoles.WarehouseKeeper]);
        await topology.CreateUserAsync("topo.resp.admin", [AppRoles.Administrator]);
        var hub = await topology.ScalarAsync<short>("SELECT id FROM inventory.warehouse WHERE code = 'UTN'");
        using var admin = await topology.SignInAsync("topo.resp.admin");

        using var assigned = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/inventory/warehouses/{hub}/responsibles", UriKind.Relative),
            new ResponsiblesRequest([new ResponsibleEntry(keeper, "KEEPER")]), Ct);

        assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(Ct));
        (await assigned.Content.ReadFromJsonAsync<List<ResponsibleDto>>(Ct))!.ShouldHaveSingleItem().EmployeeCode
            .ShouldNotBe("?", "the Inventory service named the employee through the Identity service");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await topology.ScalarAsync<long>("SELECT count(*) FROM iam.employee_responsibility WHERE employee_id = @e", ("e", keeper)) == 0
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        using var session = await topology.SignInAsync("topo.resp.keeper");
        var me = await session.GetFromJsonAsync<CurrentUserResponse>(new Uri("/bff/me", UriKind.Relative), Ct);
        me!.HomeWarehouseIds.ShouldBe([hub], "the Identity service derived the hub from the event it consumed over RabbitMQ");
        (await topology.ScalarAsync<long>(
                "SELECT count(*) FROM inventory.outbox_message WHERE type LIKE '%.ResponsibilitiesChanged,%' AND processed_at IS NOT NULL"))
            .ShouldBeGreaterThan(0, "Inventory published the change to the broker");
    }
}
