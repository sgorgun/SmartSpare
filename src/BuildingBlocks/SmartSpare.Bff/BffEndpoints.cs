using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Bff;

/// <summary>
/// Browser-session endpoints under <c>/bff</c> (STAGE_3_PLAN section 1.2, ADR 0014, ADR 0016). Unversioned, because they
/// are plumbing of the web client, not public API. Every unsafe request needs an antiforgery token, sign-in included.
/// </summary>
public static class BffEndpoints
{
    private static readonly LoginRequestValidator LoginValidator = new();

    public static RouteGroupBuilder MapBff(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bff")
            .WithTags("BFF")
            .AddEndpointFilter<AntiforgeryValidationFilter>();

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(BffDefaults.LoginRateLimitPolicy)
            .WithName("BffLogin");

        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .WithName("BffLogout");

        group.MapGet("/me", Me)
            .AllowAnonymous()
            .WithName("BffMe");

        group.MapGet("/antiforgery", Antiforgery)
            .AllowAnonymous()
            .WithName("BffAntiforgery");

        return group;
    }

    /// <summary>
    /// Checks the credentials through <see cref="ICredentialVerifier"/> and signs in with exactly the returned claims.
    /// 204 with the cookie on success; every failure, including an invalid body, is the same 400 SS-AUTH-001.
    /// </summary>
    internal static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request, ICredentialVerifier verifier, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (!(await LoginValidator.ValidateAsync(request, cancellationToken).ConfigureAwait(false)).IsValid)
        {
            return InvalidCredentials();
        }

        var result = await verifier.VerifyAsync(request.UserName, request.Password, http.Connection.RemoteIpAddress, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return InvalidCredentials();
        }

        var properties = new AuthenticationProperties { IsPersistent = request.RememberMe };
        properties.Items[BffDefaults.SessionIdItem] = Guid.NewGuid().ToString("N");
        properties.Items[BffDefaults.ValidatedAtItem] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        await http.SignInAsync(BffDefaults.CookieScheme, BffCookieEvents.CreatePrincipal(result.Value.Claims), properties)
            .ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    internal static async Task<NoContent> LogoutAsync(HttpContext http, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        await http.SignOutAsync(BffDefaults.CookieScheme).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    /// <summary>The current user from the claims, without a database call.</summary>
    internal static Ok<CurrentUserResponse> Me(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return TypedResults.Ok(CurrentUserResponse.Anonymous);
        }

        return TypedResults.Ok(new CurrentUserResponse(
            IsAuthenticated: true,
            UserId: Guid.TryParse(user.FindFirstValue(AppClaimTypes.Subject), out var id) ? id : null,
            UserName: user.FindFirstValue(AppClaimTypes.Name),
            Roles: [.. user.FindAll(AppClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal)],
            BrandIds: [.. user.FindAll(AppClaimTypes.Brand).Select(c => ParseShort(c.Value)).OfType<short>().Order()],
            HomeWarehouseId: ParseShort(user.FindFirstValue(AppClaimTypes.Warehouse))));
    }

    /// <summary>A fresh request token for the current principal (fallback; normally it comes with the prerendered page).</summary>
    internal static Ok<AntiforgeryTokenResponse> Antiforgery(IAntiforgery antiforgery, HttpContext http)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!, tokens.HeaderName ?? BffDefaults.AntiforgeryHeaderName));
    }

    private static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Sign-in failed",
            detail: "The user name or password is incorrect.",
            extensions: new Dictionary<string, object?> { [ProblemDetailsRegistration.CodeKey] = ProblemCodes.InvalidCredentials });

    private static short? ParseShort(string? value) =>
        short.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
