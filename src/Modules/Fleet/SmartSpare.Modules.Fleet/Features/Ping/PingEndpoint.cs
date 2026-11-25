using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;

namespace SmartSpare.Modules.Fleet.Features.Ping;

internal static class PingEndpoint
{
    /// <summary>GET /api/fleet/ping — liveness of the module's request pipeline.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/ping", async (IQueryHandler<PingQuery, PingResponse> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new PingQuery(), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .WithName("FleetPing")
            .WithSummary("Checks that the Fleet module answers.")
            .WithDescription("Operational, unversioned (ADR 0012); not in the API document. Requires a signed-in user.")
            .Produces<PingResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AsOperational();
}
