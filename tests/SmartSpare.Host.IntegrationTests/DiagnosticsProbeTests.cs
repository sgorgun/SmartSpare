using System.Net;
using System.Net.Http.Json;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The diagnostics probe in the single host (STAGE_4_PLAN 5.3): a signed-in user's request writes an event to Identity's
/// outbox, and the in-process transport delivers it to Inventory's consumer, whose inbox records it under its stable
/// name. The services topology runs the same path over RabbitMQ (4g).
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class DiagnosticsProbeTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private HostFactory _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_probe_sent_through_identity_is_handled_once_by_inventory()
    {
        await database.CreateUserAsync("it.probe", [AppRoles.InventoryPlanner], []);
        using var browser = _host.NewBrowser();
        using (var login = await browser.LoginAsync("it.probe", IdentityDatabaseFixture.Password))
        {
            login.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var response = await browser.PostAsync("/api/identity/diagnostics/probe", token: await browser.AntiforgeryTokenAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probe = await response.Content.ReadFromJsonAsync<ProbeResponse>(TestContext.Current.CancellationToken);
        var eventId = await database.ScalarAsync<Guid>(
            "SELECT id FROM iam.outbox_message WHERE payload ->> 'probeId' = @probe", ("probe", probe!.ProbeId.ToString()));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        long handled;
        while ((handled = await database.ScalarAsync<long>(
                   "SELECT count(*) FROM inventory.inbox_message WHERE message_id = @id AND consumer = 'inventory.diagnostics-probe'", ("id", eventId))) == 0
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        handled.ShouldBe(1);
        (await database.ScalarAsync<bool>("SELECT processed_at IS NOT NULL FROM iam.outbox_message WHERE id = @id", ("id", eventId))).ShouldBeTrue();
    }

    [Fact]
    public async Task Sending_a_probe_needs_a_signed_in_user()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.PostAsync("/api/identity/diagnostics/probe", token: await browser.AntiforgeryTokenAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed record ProbeResponse(Guid ProbeId);
}
