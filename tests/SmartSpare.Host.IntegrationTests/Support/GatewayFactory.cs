extern alias gateway;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using gateway::SmartSpare.Gateway;
using SmartSpare.Infrastructure.Security;
using Yarp.ReverseProxy.Forwarder;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// The real gateway (<c>SmartSpare.Gateway</c>, STAGE_4_PLAN 4e) in memory. Every outgoing call (YARP, the Identity
/// client, the warm-up) goes to an in-memory destination by host name: <c>identity</c> to a real Identity service host,
/// every other service to a <see cref="RecordingService"/> that echoes what it received.
/// </summary>
public sealed class GatewayFactory(IdentityServiceFactory identity) : WebApplicationFactory<GatewayProgram>
{
    public RecordingService Recorder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting($"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.SigningKey)}", TestKeys.SigningKeyPem);

        builder.ConfigureTestServices(services =>
        {
            var destinations = new DestinationHandler(identity.Server.CreateHandler(), Recorder);
            services.AddSingleton<IForwarderHttpClientFactory>(new TestForwarderFactory(destinations));
            services.AddHttpClient(GatewayClients.Identity).ConfigurePrimaryHttpMessageHandler(() => destinations);
            services.AddHttpClient(WarmupEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => destinations);
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestClientIp>();
        });
    }

    internal Browser NewBrowser() => Browser.Over(Server.CreateHandler(), TestClientIp.Unique());

    private sealed class TestForwarderFactory(HttpMessageHandler destinations) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(destinations, disposeHandler: false);
    }

    /// <summary>Sends each request to the destination named by its host.</summary>
    private sealed class DestinationHandler(HttpMessageHandler identityService, RecordingService recorder) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _identity = new(identityService, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == "identity"
                ? _identity.SendAsync(request, cancellationToken)
                : recorder.HandleAsync(request);
    }
}

/// <summary>A service that records every request it gets and echoes its path and headers as JSON.</summary>
public sealed class RecordingService
{
    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    internal Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        var received = new ReceivedRequest(
            request.Method.Method,
            request.RequestUri!.Host,
            request.RequestUri.PathAndQuery,
            request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
        Received.Enqueue(received);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(received) });
    }
}

public sealed record ReceivedRequest(string Method, string Service, string Path, Dictionary<string, string> Headers);
