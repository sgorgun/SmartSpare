using System.Net;
using SmartSpare.ApiContracts;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>Fixed-window rate limit on <c>POST /bff/login</c>: 10 per minute per client IP (STAGE_3_PLAN section 4).</summary>
public sealed class RateLimitTests(FakeIdentityHostFactory factory) : IClassFixture<FakeIdentityHostFactory>
{
    [Fact]
    public async Task The_eleventh_sign_in_attempt_in_a_minute_from_one_ip_gets_429_with_retry_after()
    {
        using var attacker = factory.NewBrowser();
        var token = await attacker.AntiforgeryTokenAsync();

        for (var attempt = 1; attempt <= BffDefaults.LoginPermitsPerWindow; attempt++)
        {
            using var allowed = await attacker.LoginAsync("someone", "guess " + attempt, token: token);
            allowed.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"attempt {attempt} is within the limit");
        }

        using var limited = await attacker.LoginAsync("someone", "one more guess", token: token);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var retryAfter = limited.Headers.RetryAfter?.Delta;
        retryAfter.ShouldNotBeNull();
        retryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(1), BffDefaults.LoginWindow);
        var problem = await Problem.ReadAsync(limited);
        problem.Code.ShouldBe(ProblemCodes.TooManyRequests);
        problem.TraceId.ShouldNotBeNullOrEmpty();

        // Another client is not affected: the limit is per IP.
        using var neighbour = factory.NewBrowser();
        using var other = await neighbour.LoginAsync("someone", "guess");
        other.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
