namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>GET /bff/antiforgery</c>: a request token for the current principal and the header to send it in.</summary>
public sealed record AntiforgeryTokenResponse(string RequestToken, string HeaderName);
