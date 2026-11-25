using System.Collections.Concurrent;
using System.Security.Claims;
using SmartSpare.Bff;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Gateway;

/// <summary>
/// The internal token of each session (STAGE_4_PLAN 2.2): minted once, reused until 30 s before it expires. The key is
/// the session id and the forwarded claims, so a role change re-issued by the session check gets a new token at once.
/// </summary>
public sealed class SessionTokenCache(InternalTokenIssuer issuer, TimeProvider time)
{
    /// <summary>A cached token is not used in its last 30 s, so it can't expire on the way to the service.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private const int PruneAbove = 1_000;

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset RenewAt)> _tokens = new(StringComparer.Ordinal);

    public int Count => _tokens.Count;

    public string GetToken(ClaimsPrincipal user, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var now = time.GetUtcNow();
        var key = sessionId + "|" + Fingerprint(user);
        if (_tokens.TryGetValue(key, out var cached) && now < cached.RenewAt)
        {
            return cached.Token;
        }

        var token = issuer.CreateToken(user, sessionId);
        _tokens[key] = (token, now + InternalTokenDefaults.Lifetime - RenewBefore);
        if (_tokens.Count > PruneAbove)
        {
            Prune(now);
        }

        return token;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _tokens.Where(e => e.Value.RenewAt <= now).ToList())
        {
            _tokens.TryRemove(entry);
        }
    }

    private static string Fingerprint(ClaimsPrincipal user) =>
        string.Join("\n", user.Claims.Select(c => c.Type + "=" + c.Value).Order(StringComparer.Ordinal));
}
