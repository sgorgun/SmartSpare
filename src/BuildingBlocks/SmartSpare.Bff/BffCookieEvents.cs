using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Bff;

/// <summary>
/// Events of the auth cookie (STAGE_3_PLAN section 1.3, ADR 0016 section 3).
/// <list type="bullet">
/// <item>Every <see cref="SessionCheckOptions.CheckInterval"/> (30 seconds by default) the session is re-validated through
/// <see cref="ICredentialVerifier.ValidateSessionAsync"/>: a revoked session (stamp rotated, user deactivated or anonymised)
/// is rejected, so the request is anonymous and gets 401; changed roles or brands re-issue the cookie with the new
/// claims. This replaces Identity's <c>SecurityStampValidator</c>, so the host and the gateway behave the same.</item>
/// <item>For <c>/api</c> and <c>/bff</c> the cookie handler answers 401 and 403 instead of redirecting to HTML.</item>
/// </list>
/// </summary>
internal sealed class BffCookieEvents(ICredentialVerifier verifier, Microsoft.Extensions.Options.IOptions<SessionCheckOptions> sessionOptions) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal;
        if (principal is null)
        {
            return;
        }

        var now = context.Options.TimeProvider!.GetUtcNow();
        var validatedAt = context.Properties.Items.TryGetValue(BffDefaults.ValidatedAtItem, out var value)
                          && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : context.Properties.IssuedUtc ?? DateTimeOffset.MinValue;

        if (now - validatedAt < sessionOptions.Value.CheckInterval)
        {
            return;
        }

        var userId = Guid.TryParse(principal.FindFirstValue(AppClaimTypes.Subject), out var id) ? id : Guid.Empty;
        var stamp = principal.FindFirstValue(IdentityClaimTypes.SecurityStamp);
        var session = userId == Guid.Empty || string.IsNullOrEmpty(stamp)
            ? null
            : await verifier.ValidateSessionAsync(userId, stamp, context.HttpContext.RequestAborted).ConfigureAwait(false);

        if (session is not { IsSuccess: true })
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(BffDefaults.CookieScheme).ConfigureAwait(false);
            return;
        }

        if (!SameClaims(principal.Claims, session.Value.Claims))
        {
            context.ReplacePrincipal(CreatePrincipal(session.Value.Claims));
        }

        context.Properties.Items[BffDefaults.ValidatedAtItem] = now.ToString("O", CultureInfo.InvariantCulture);
        context.ShouldRenew = true;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsApiRequest(context.Request) ? WriteStatus(context.Response, StatusCodes.Status401Unauthorized) : base.RedirectToLogin(context);
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsApiRequest(context.Request) ? WriteStatus(context.Response, StatusCodes.Status403Forbidden) : base.RedirectToAccessDenied(context);
    }

    /// <summary>The cookie principal built from exactly the claims the Identity module returned.</summary>
    public static ClaimsPrincipal CreatePrincipal(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, BffDefaults.CookieScheme, AppClaimTypes.Name, AppClaimTypes.Role));

    private static bool SameClaims(IEnumerable<Claim> current, IEnumerable<Claim> fresh) =>
        Keys(current).SequenceEqual(Keys(fresh));

    private static IEnumerable<string> Keys(IEnumerable<Claim> claims) =>
        claims.Select(c => c.Type + "\n" + c.Value).Order(StringComparer.Ordinal);

    private static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets the status only; the status-code pages write the ProblemDetails (with trace id and code).</summary>
    private static Task WriteStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
