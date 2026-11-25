using System.ComponentModel;

namespace SmartSpare.ApiContracts.Identity;

/// <summary>Body of <c>POST /bff/login</c>. <see cref="RememberMe"/> makes the cookie persistent (8 h at most).</summary>
[Description("Credentials for signing in.")]
public sealed record LoginRequest(
    [property: Description("User name, 1 to 50 characters.")] string UserName,
    [property: Description("Password, 1 to 256 characters.")] string Password,
    [property: Description("True keeps the cookie after the browser closes (8 hours at most).")] bool RememberMe = false);
