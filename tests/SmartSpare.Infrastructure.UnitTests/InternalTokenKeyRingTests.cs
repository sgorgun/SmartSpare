using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Infrastructure;

/// <summary>Keys of internal tokens (ADR 0016 section 2).</summary>
public sealed class InternalTokenKeyRingTests
{
    private static readonly string Pem = NewPem();

    [Fact]
    public void Without_configuration_there_is_no_key_at_all()
    {
        using var ring = new InternalTokenKeyRing(Options.Create(new InternalTokenOptions()));

        ring.SigningKey.ShouldBeNull();
        ring.ValidationKeys.ShouldBeEmpty();
    }

    [Fact]
    public void The_key_id_is_the_rfc7638_thumbprint_and_is_stable()
    {
        using var first = Ring(Pem);
        using var second = Ring(Pem);

        first.SigningKey!.KeyId.ShouldNotBeNullOrEmpty();
        second.SigningKey!.KeyId.ShouldBe(first.SigningKey.KeyId);
        first.ValidationKeys.ShouldHaveSingleItem().KeyId.ShouldBe(first.SigningKey.KeyId);
    }

    [Fact]
    public void Only_p256_keys_are_accepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Should.Throw<InvalidOperationException>(() => Ring(p384.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public async Task A_host_configured_with_the_public_jwks_validates_tokens_of_the_signing_host()
    {
        using var gateway = Ring(Pem);
        using var service = new InternalTokenKeyRing(Options.Create(new InternalTokenOptions { Jwks = gateway.PublicJwks() }));

        service.SigningKey.ShouldBeNull("a service holds only the public key");
        var result = await Validate(Sign(gateway), service);

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
    }

    [Fact]
    public async Task Disposing_one_ring_does_not_break_another_ring_with_the_same_key()
    {
        // Signature providers are cached per key id; each ring keeps its own cache (two hosts in one process,
        // or the previous ring during key rotation).
        var disposed = Ring(Pem);
        _ = Sign(disposed);
        disposed.Dispose();

        using var alive = Ring(Pem);
        var token = Sign(alive);

        (await Validate(token, alive)).IsValid.ShouldBeTrue();
    }

    private static InternalTokenKeyRing Ring(string pem) => new(Options.Create(new InternalTokenOptions { SigningKey = pem }));

    private static string Sign(InternalTokenKeyRing ring) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = InternalTokenDefaults.Issuer,
        Audience = InternalTokenDefaults.Audience,
        Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())]),
        Expires = DateTime.UtcNow.AddMinutes(2),
        SigningCredentials = new SigningCredentials(ring.SigningKey, SecurityAlgorithms.EcdsaSha256),
    });

    private static Task<TokenValidationResult> Validate(string token, InternalTokenKeyRing ring) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = InternalTokenDefaults.Issuer,
            ValidAudience = InternalTokenDefaults.Audience,
            IssuerSigningKeys = ring.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });

    private static string NewPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
