namespace SmartSpare.ApiContracts;

/// <summary>HTTP header names shared by the BFF and the web client.</summary>
public static class HttpHeaderNames
{
    /// <summary>Carries the antiforgery request token on every unsafe request (STAGE_3_PLAN section 1.5).</summary>
    public const string Antiforgery = "X-XSRF-TOKEN";
}
