using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Topology.Tests;

/// <summary>
/// The services topology end to end (STAGE_4_PLAN section 10, #57): sign-in through the gateway, both services reached
/// with a minted token, the diagnostics probe from Identity to Inventory over RabbitMQ, and one trace of its delivery
/// linked to the request; <c>/internal</c> and service health are never reachable through the gateway.
/// </summary>
[Trait("Category", "Topology")]
[Collection(TopologyGroup.Name)]
public sealed class TopologyTests(TopologyFixture topology)
{
    [Fact]
    public async Task A_user_signs_in_through_the_gateway_and_reaches_both_services()
    {
        await topology.CreateUserAsync("topo.signin", [AppRoles.InventoryPlanner]);
        using var browser = await SignInAsync("topo.signin");

        using var identity = await browser.GetAsync(new Uri("/api/identity/ping", UriKind.Relative), Ct);
        using var inventory = await browser.GetAsync(new Uri("/api/inventory/ping", UriKind.Relative), Ct);

        identity.StatusCode.ShouldBe(HttpStatusCode.OK);
        inventory.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_versioned_inventory_api_reads_the_database_through_the_gateway_as_the_service_login()
    {
        await topology.CreateUserAsync("topo.reader", [AppRoles.ServiceEngineer]);
        using var browser = await SignInAsync("topo.reader");

        // smartspare_inventory reads inventory.warehouse, catalog.part and the Platform's ref tables.
        var warehouses = await browser.GetFromJsonAsync<List<SmartSpare.ApiContracts.Inventory.WarehouseResponse>>(
            new Uri("/api/v1/inventory/warehouses", UriKind.Relative), Ct);
        using var parts = await browser.GetAsync(new Uri("/api/v1/inventory/parts?pageSize=5", UriKind.Relative), Ct);
        using var unknown = await browser.GetAsync(new Uri("/api/v1/inventory/parts/999999999", UriKind.Relative), Ct);
        using var stock = await browser.GetAsync(new Uri("/api/v1/inventory/stock?pageSize=5", UriKind.Relative), Ct);
        using var internalQuery = await browser.GetAsync(new Uri("/internal/v1/inventory/warehouses", UriKind.Relative), Ct);

        warehouses!.Select(w => w.Code).ShouldContain("VLN");
        parts.StatusCode.ShouldBe(HttpStatusCode.OK);
        stock.StatusCode.ShouldBe(HttpStatusCode.OK, "the stock overview reads inventory, catalog and ref as smartspare_inventory");
        internalQuery.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the gateway never routes /internal");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the service's ProblemDetails passes through the gateway");
    }

    [Fact]
    public async Task The_probe_travels_from_identity_to_inventory_over_rabbitmq_in_one_linked_trace()
    {
        await topology.CreateUserAsync("topo.probe", [AppRoles.InventoryPlanner]);
        using var browser = await SignInAsync("topo.probe");

        using var response = await browser.PostAsJsonAsync(new Uri("/api/identity/diagnostics/probe", UriKind.Relative), new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var probeId = (await response.Content.ReadFromJsonAsync<ProbeResponse>(Ct))!.ProbeId;
        var eventId = (await topology.ScalarAsync<Guid>(
            "SELECT id FROM iam.outbox_message WHERE payload ->> 'probeId' = @probe", ("probe", probeId.ToString())));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await topology.ScalarAsync<long>(
                   "SELECT count(*) FROM inventory.inbox_message WHERE message_id = @id AND consumer = 'inventory.diagnostics-probe'", ("id", eventId)) == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "Inventory never handled the probe");
            await Task.Delay(250, Ct);
        }

        // The request's trace: the gateway's server span and the Identity service's server span below it.
        var requests = topology.Activities.Where(a => a.Kind == ActivityKind.Server && a.GetTagItem("url.path") as string == "/api/identity/diagnostics/probe").ToList();
        requests.Count.ShouldBe(2, "one server span in the gateway, one in the Identity service");
        requests.Select(a => a.TraceId).Distinct().Count().ShouldBe(1, "the gateway propagated its trace to the service");
        var traceId = requests[0].TraceId;
        (await topology.ScalarAsync<string>("SELECT trace_parent FROM iam.outbox_message WHERE id = @id", ("id", eventId)))
            .ShouldNotBeNull().ShouldContain(traceId.ToHexString(), Case.Sensitive, "the outbox kept the request's traceparent");

        // The delivery's trace: the consume span in Inventory is the child of the publish span that delivered it, and
        // that publish span links to the request. (An earlier publish may have been returned while Inventory was still
        // declaring its queue; the outbox retried it, so the consume span picks the right one.)
        // RabbitMQ.Client traces too: its own publish span may sit between ours and the consumer, so walk up the parents.
        var consume = await WaitForAsync(a => a.Source.Name == IntegrationEventDelivery.ActivitySourceName && a.Kind == ActivityKind.Consumer
                                              && a.GetTagItem("messaging.message.id") as string == eventId.ToString());
        consume.DisplayName.ShouldStartWith("inventory.diagnostics-probe.");
        var publish = Ancestors(consume).FirstOrDefault(a => a.Source.Name == IntegrationEventDelivery.ActivitySourceName && a.Kind == ActivityKind.Producer)
            .ShouldNotBeNull("the consume span descends from SmartSpare's publish span");
        publish.GetTagItem("messaging.message.id").ShouldBe(eventId.ToString());
        publish.TraceId.ShouldBe(consume.TraceId);
        publish.Links.Select(l => l.Context.TraceId).ShouldContain(traceId, "the delivery's trace links back to the request's");
    }

    [Fact]
    public async Task Internal_endpoints_and_service_health_are_not_reachable_through_the_gateway()
    {
        await topology.CreateUserAsync("topo.internal", [AppRoles.Administrator]);
        using var browser = await SignInAsync("topo.internal");
        var before = topology.Forwarded.Count;

        foreach (var path in new[] { "/internal/v1/identity/credential-checks", "/internal/v1/inventory/x", "/identity/health/ready", "/inventory/health/live" })
        {
            using var get = await browser.GetAsync(new Uri(path, UriKind.Relative), Ct);
            using var post = await browser.PostAsJsonAsync(new Uri(path, UriKind.Relative), new { }, Ct);
            ((int)get.StatusCode).ShouldNotBe(200, path);
            ((int)post.StatusCode).ShouldNotBe(200, path);
        }

        topology.Forwarded.Skip(before).ShouldBeEmpty("nothing was forwarded to a service");
    }

    private IEnumerable<Activity> Ancestors(Activity activity)
    {
        var byId = topology.Activities.GroupBy(a => a.SpanId).ToDictionary(g => g.Key, g => g.First());
        for (var current = activity; byId.TryGetValue(current.ParentSpanId, out var parent); current = parent)
        {
            yield return parent;
        }
    }

    private async Task<Activity> WaitForAsync(Func<Activity, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (topology.Activities.FirstOrDefault(predicate) is { } activity)
            {
                return activity;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the span was never recorded");
            await Task.Delay(100, Ct);
        }
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = new HttpClient(new CookieContainerHandler(topology.Gateway.Server.CreateHandler())) { BaseAddress = new Uri("https://localhost") };
        var token = (await client.GetFromJsonAsync<AntiforgeryTokenResponse>(new Uri("/bff/antiforgery", UriKind.Relative), Ct))!;
        client.DefaultRequestHeaders.Add(token.HeaderName, token.RequestToken);
        using var login = await client.PostAsJsonAsync(new Uri("/bff/login", UriKind.Relative), new LoginRequest(userName, TopologyFixture.Password, false), Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The token is bound to the user, so a signed-in client fetches a new one, as the web client does.
        var signedIn = (await client.GetFromJsonAsync<AntiforgeryTokenResponse>(new Uri("/bff/antiforgery", UriKind.Relative), Ct))!;
        client.DefaultRequestHeaders.Remove(signedIn.HeaderName);
        client.DefaultRequestHeaders.Add(signedIn.HeaderName, signedIn.RequestToken);
        return client;
    }

    private sealed record ProbeResponse(Guid ProbeId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

/// <summary>Keeps the gateway's cookies between requests, like a browser (HTTPS, so the <c>__Host-</c> cookies are sent).</summary>
internal sealed class CookieContainerHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = _cookies.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(header))
        {
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                _cookies.SetCookies(request.RequestUri!, setCookie);
            }
        }

        return response;
    }
}
