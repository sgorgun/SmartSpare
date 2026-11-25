using Microsoft.AspNetCore.Builder;

namespace SmartSpare.Infrastructure.Http;

/// <summary>
/// Marks an operational endpoint, such as a module ping or a diagnostics probe (ADR 0012): it stays unversioned under
/// <c>/api/{module}</c>, and the gateway's <c>ops-{module}</c> route reaches it. Every other module endpoint belongs under
/// <c>/api/v1/{module}</c>; a test fails for an unversioned endpoint without this marker (product owner, 25 Nov 2026).
/// </summary>
public sealed class OperationalEndpointMetadata
{
    public static OperationalEndpointMetadata Instance { get; } = new();

    private OperationalEndpointMetadata()
    {
    }
}

public static class OperationalEndpointExtensions
{
    public static TBuilder AsOperational<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(OperationalEndpointMetadata.Instance);
}
