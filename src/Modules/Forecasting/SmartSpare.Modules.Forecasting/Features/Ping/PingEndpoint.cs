using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;

namespace SmartSpare.Modules.Forecasting.Features.Ping;

internal static class PingEndpoint
{
    /// <summary>GET /api/forecasting/ping — liveness of the module's request pipeline.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/ping", async (IQueryHandler<PingQuery, PingResponse> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new PingQuery(), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .WithName("ForecastingPing")
            .Produces<PingResponse>();
}
