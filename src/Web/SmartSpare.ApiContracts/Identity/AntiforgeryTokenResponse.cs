using System.ComponentModel;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>GET /bff/antiforgery</c>: a request token for the current principal and the header to send it in.</summary>
[Description("An antiforgery request token and the header to send it in.")]
public sealed record AntiforgeryTokenResponse(
    [property: Description("The request token, bound to the current user.")] string RequestToken,
    [property: Description("Header to send it in on unsafe requests: X-XSRF-TOKEN.")] string HeaderName);
