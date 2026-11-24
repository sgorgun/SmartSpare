using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The trace of a request includes its database commands (ADR 0018, docs/operations/TRACING.md): the host's tracer
/// subscribes to the Npgsql driver's activity source. In-memory host, no Docker.
/// </summary>
public sealed class TracingTests(HostFactory factory) : IClassFixture<HostFactory>
{
    [Fact]
    public void The_host_traces_database_commands()
    {
        _ = factory.Services; // starts the host and with it the tracer provider

        using var npgsql = new ActivitySource(ServiceDefaultsExtensions.NpgsqlActivitySource);
        using var unrelated = new ActivitySource("SmartSpare.Tests.NotTraced");

        npgsql.HasListeners().ShouldBeTrue("the tracer must subscribe to Npgsql's command spans");
        unrelated.HasListeners().ShouldBeFalse("nothing listens to every source, so the check above is meaningful");
    }
}
