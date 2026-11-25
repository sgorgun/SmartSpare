using System.Net.Http.Headers;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Bff;

/// <summary>
/// Sends the gateway's own service token (<c>sub=gateway</c>, <c>ss:svc=gateway</c>, ADR 0016 section 1) on calls the
/// gateway makes as itself, such as the Identity service's credential and session checks (STAGE_4_PLAN section 4).
/// A fresh token per request; it lives 2 minutes, and minting costs one ES256 signature.
/// </summary>
public sealed class ServiceTokenHandler(InternalTokenIssuer issuer) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.CreateServiceToken(InternalTokenDefaults.GatewayService));
        return base.SendAsync(request, cancellationToken);
    }
}
