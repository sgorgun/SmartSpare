using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Diagnostics;

/// <summary>Publishes a <see cref="DiagnosticsProbeSent"/> through Identity's outbox (STAGE_4_PLAN 5.3; removed with #55).</summary>
internal sealed record SendDiagnosticsProbe : ICommand<DiagnosticsProbeResponse>;

internal sealed class SendDiagnosticsProbeHandler(IUnitOfWork<IdentityDbContext> unitOfWork) : ICommandHandler<SendDiagnosticsProbe, DiagnosticsProbeResponse>
{
    public async Task<Result<DiagnosticsProbeResponse>> Handle(SendDiagnosticsProbe command, CancellationToken cancellationToken)
    {
        var probe = new DiagnosticsProbeSent(Guid.NewGuid());
        unitOfWork.AddIntegrationEvent(probe);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new DiagnosticsProbeResponse(probe.ProbeId));
    }
}

internal static class SendDiagnosticsProbeEndpoint
{
    /// <summary>
    /// POST /api/identity/diagnostics/probe: writes a probe event to the outbox and returns its id. Any signed-in user
    /// (the fallback policy); antiforgery applies like every unsafe request.
    /// </summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPost("/diagnostics/probe", async (ICommandHandler<SendDiagnosticsProbe, DiagnosticsProbeResponse> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new SendDiagnosticsProbe(), cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .WithName("IdentitySendDiagnosticsProbe")
            .WithSummary("Publishes a diagnostics probe event that Inventory consumes.")
            .WithDescription("Operational, unversioned (ADR 0012); proves the integration-event path end to end. Removed with #55.")
            .Produces<DiagnosticsProbeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AsOperational();
}

internal sealed record DiagnosticsProbeResponse(Guid ProbeId);
