using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Modules.Identity.Features.InternalChecks;

/// <summary>
/// The credential and session checks for the gateway (ADR 0016 section 1, STAGE_4_PLAN section 4): the same
/// <see cref="ICredentialVerifier"/> the single host calls in process, answered over HTTP. Policy <c>GatewayOnly</c>;
/// excluded from the API description. The two expected failures are ProblemDetails with the contract's error code.
/// </summary>
internal static class InternalCheckEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(IdentityInternalApi.Prefix)
            .RequireAuthorization(InternalTokenDefaults.GatewayOnlyPolicy)
            .ExcludeFromDescription();

        group.MapPost("/credential-checks", async (CredentialCheckRequest request, ICredentialVerifier verifier, CancellationToken cancellationToken) =>
        {
            var clientIp = IPAddress.TryParse(request.ClientIp, out var ip) ? ip : null;
            var result = await verifier.VerifyAsync(request.UserName, request.Password, clientIp, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? TypedResults.Ok(VerifiedSessionResponse.From(result.Value)) : result.Error.ToProblem();
        });

        group.MapPost("/session-checks", async (SessionCheckRequest request, ICredentialVerifier verifier, CancellationToken cancellationToken) =>
        {
            var result = await verifier.ValidateSessionAsync(request.UserId, request.SecurityStamp, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? TypedResults.Ok(VerifiedSessionResponse.From(result.Value)) : result.Error.ToProblem();
        });
    }
}
