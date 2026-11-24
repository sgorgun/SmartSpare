using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SmartSpare.Infrastructure.Security;

/// <summary>Configuration of internal tokens (section <c>InternalTokens</c>).</summary>
public sealed class InternalTokenOptions
{
    public const string SectionName = "InternalTokens";

    /// <summary>
    /// PEM (PKCS#8) of the ECDSA P-256 private key that signs internal tokens. Only the minting host holds it: the
    /// gateway in production (Key Vault), user-secrets locally, an in-memory key in tests. Never in a file of the
    /// repository or an image. The single host derives its validation key from it when it is set.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// JWKS document (JSON) with the public keys that validate internal tokens, each with a <c>kid</c>; two keys during
    /// rotation. This is what services get (ADR 0016 section 2).
    /// </summary>
    public string? Jwks { get; set; }
}

/// <summary>
/// Keys of internal tokens, loaded once from <see cref="InternalTokenOptions"/>: the signing key (if this host mints)
/// and the validation keys. Without any key every internal token is rejected. Key ids are RFC 7638 JWK thumbprints.
/// </summary>
public sealed class InternalTokenKeyRing : IDisposable
{
    private readonly List<ECDsa> _owned = [];

    public InternalTokenKeyRing(IOptions<InternalTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        var validation = new List<SecurityKey>();

        // IdentityModel caches signature providers per key id in a process-wide factory by default. The keys below are
        // disposed with this ring, so their providers must be cached with the ring too, not shared with other rings
        // (another host in the process, or the ring of a previous key during rotation).
        var crypto = new CryptoProviderFactory();

        if (!string.IsNullOrWhiteSpace(value.SigningKey))
        {
            var privateKey = Own(ECDsa.Create());
            privateKey.ImportFromPem(value.SigningKey);
            if (privateKey.KeySize != 256)
            {
                throw new InvalidOperationException("InternalTokens:SigningKey must be an ECDSA P-256 key (ES256).");
            }

            var keyId = KeyIdOf(privateKey);
            SigningKey = new ECDsaSecurityKey(privateKey) { KeyId = keyId, CryptoProviderFactory = crypto };

            var publicKey = Own(ECDsa.Create(privateKey.ExportParameters(includePrivateParameters: false)));
            validation.Add(new ECDsaSecurityKey(publicKey) { KeyId = keyId, CryptoProviderFactory = crypto });
        }

        if (!string.IsNullOrWhiteSpace(value.Jwks))
        {
            validation.AddRange(new JsonWebKeySet(value.Jwks).GetSigningKeys());
        }

        ValidationKeys = validation;
    }

    /// <summary>The key that signs internal tokens, or <see langword="null"/> on a host that only validates.</summary>
    public ECDsaSecurityKey? SigningKey { get; }

    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The public JWKS of <see cref="SigningKey"/>, to configure the hosts that validate.</summary>
    public string PublicJwks()
    {
        if (SigningKey is null)
        {
            throw new InvalidOperationException("No signing key is configured.");
        }

        using var publicKey = ECDsa.Create(SigningKey.ECDsa.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicKey) { KeyId = SigningKey.KeyId });
        jwk.Use = JsonWebKeyUseNames.Sig;
        jwk.Alg = SecurityAlgorithms.EcdsaSha256;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = jwk.Kty, use = jwk.Use, alg = jwk.Alg, kid = jwk.Kid, crv = jwk.Crv, x = jwk.X, y = jwk.Y } },
        });
    }

    public void Dispose()
    {
        foreach (var key in _owned)
        {
            key.Dispose();
        }

        _owned.Clear();
    }

    private static string KeyIdOf(ECDsa key)
    {
        using var publicKey = ECDsa.Create(key.ExportParameters(includePrivateParameters: false));
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicKey));
        return Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
    }

    private ECDsa Own(ECDsa key)
    {
        _owned.Add(key);
        return key;
    }
}
