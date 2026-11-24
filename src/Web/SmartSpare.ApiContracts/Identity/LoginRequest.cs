namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>POST /bff/login</c>. <see cref="RememberMe"/> makes the cookie persistent (8 h at most).</summary>
public sealed record LoginRequest(string UserName, string Password, bool RememberMe = false);
