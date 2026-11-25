using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// <see cref="ICredentialVerifier"/> over HTTP, for the gateway (ADR 0015 §4, STAGE_4_PLAN section 4). The host
/// registers it as a typed client whose handlers add the gateway's service token, resilience and service discovery.
/// <list type="bullet">
/// <item>200: the verified session.</item>
/// <item>The two expected failures keep the semantics of stage 3: <see cref="IdentityContractErrors.InvalidCredentials"/>
/// and <see cref="IdentityContractErrors.SessionRevoked"/>, recognised by their error code.</item>
/// <item>Anything else, including transport failures and timeouts, throws <see cref="DependencyUnavailableException"/>
/// (503 to the browser), so an infrastructure error is never reported as a wrong password.</item>
/// </list>
/// </summary>
public sealed class HttpCredentialVerifier(HttpClient client) : ICredentialVerifier
{
    public Task<Result<VerifiedSession>> VerifyAsync(string userName, string password, IPAddress? clientIp, CancellationToken cancellationToken) =>
        SendAsync(IdentityInternalApi.CredentialChecks, new CredentialCheckRequest(userName, password, clientIp?.ToString()), cancellationToken);

    public Task<Result<VerifiedSession>> ValidateSessionAsync(Guid userId, string securityStamp, CancellationToken cancellationToken) =>
        SendAsync(IdentityInternalApi.SessionChecks, new SessionCheckRequest(userId, securityStamp), cancellationToken);

    private async Task<Result<VerifiedSession>> SendAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), request, cancellationToken).ConfigureAwait(false);
        }
        // Transport failures, timeouts (not the caller's cancellation) and the resilience handler's rejections. Polly's
        // exceptions are matched by namespace, so the contract needs no Polly reference.
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                                   || ex.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true)
        {
            throw Unavailable($"The Identity service did not answer {path}.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<VerifiedSessionResponse>(cancellationToken).ConfigureAwait(false)
                    ?? throw Unavailable($"The Identity service answered {path} without a body.");
                return body.ToSession();
            }

            var code = await ErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            if (code == IdentityContractErrors.InvalidCredentials.Code)
            {
                return IdentityContractErrors.InvalidCredentials;
            }

            if (code == IdentityContractErrors.SessionRevoked.Code)
            {
                return IdentityContractErrors.SessionRevoked;
            }

            throw Unavailable($"The Identity service answered {path} with {(int)response.StatusCode}.");
        }
    }

    /// <summary>The <c>code</c> of a ProblemDetails body, or null.</summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json"))
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DependencyUnavailableException Unavailable(string message, Exception? inner = null) =>
        inner is null ? new(message) { Service = IdentityModule.Name } : new(message, inner) { Service = IdentityModule.Name };
}
