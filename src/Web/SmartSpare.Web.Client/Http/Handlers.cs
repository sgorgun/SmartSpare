using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using SmartSpare.ApiContracts;

namespace SmartSpare.Web.Client.Http;

/// <summary>
/// Adds the antiforgery request token to every unsafe request (POST, PUT, PATCH, DELETE) as <c>X-XSRF-TOKEN</c>
/// (STAGE_3_PLAN section 1.5). The token comes from <see cref="AntiforgeryStateProvider"/>, which Blazor fills from the
/// prerendered page; it is bound to the signed-in user, which is why sign-in and sign-out reload the page.
/// </summary>
public sealed class AntiforgeryHandler(AntiforgeryStateProvider antiforgery) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsUnsafe(request.Method) && antiforgery.GetAntiforgeryToken() is { Value: { Length: > 0 } token })
        {
            request.Headers.Remove(HttpHeaderNames.Antiforgery);
            request.Headers.Add(HttpHeaderNames.Antiforgery, token);
        }

        return base.SendAsync(request, cancellationToken);
    }

    internal static bool IsUnsafe(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Delete;
}

/// <summary>
/// Session handling of every API call (STAGE_3_PLAN section 1.4): a 401 means the session ended (cookie expired or the
/// security stamp was rejected), so the app reloads to <c>/login?returnUrl=…</c>; a 403 is a UI error shown in the
/// snackbar with the trace id. Other errors are left to the caller.
/// </summary>
public sealed class UnauthorizedHandler(NavigationManager navigation, ISnackbar snackbar, ProblemMessages messages) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            navigation.NavigateTo(LoginLinks.For(navigation), forceLoad: true);
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            var problem = await ProblemInfo.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            snackbar.Add(messages.Describe(problem), Severity.Error);
        }

        return response;
    }
}

/// <summary>Links to the sign-in page that bring the user back to where they were.</summary>
public static class LoginLinks
{
    public const string LoginPath = "login";

    /// <summary><c>login?returnUrl=&lt;current path and query&gt;</c>; never points back to the sign-in page itself.</summary>
    public static string For(NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var current = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        return current.StartsWith("/" + LoginPath, StringComparison.OrdinalIgnoreCase)
            ? LoginPath
            : $"{LoginPath}?returnUrl={Uri.EscapeDataString(current)}";
    }

    /// <summary>Only local paths are followed after sign-in (no open redirect).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
