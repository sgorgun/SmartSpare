using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Security;

namespace SmartSpare.Modules.Identity;

public sealed class PasswordHashingTests
{
    private const string Password = "a demo password";

    private static readonly AppUser User = AppUser.Create(
        Guid.NewGuid(), Guid.NewGuid(), UserName.Create("tomas.r").Value, EmailAddress.Create("t@d.e").Value, "T R", null,
        "AQAAAAIAA1tgAAAAE" + new string('A', 67));

    private static PasswordHasher<AppUser> SharedHasher()
    {
        var options = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(options);
        return new PasswordHasher<AppUser>(Options.Create(options));
    }

    [Fact]
    public void New_hashes_are_v3_hmac_sha512_with_220000_iterations()
    {
        var hash = SharedHasher().HashPassword(User, Password);

        IdentityV3HashHeader.TryParse(hash, out var header).ShouldBeTrue();
        header.Prf.ShouldBe(IdentityV3HashHeader.HmacSha512);
        header.IterationCount.ShouldBe((uint)IdentityModuleInstaller.PasswordHashIterations);
        header.SaltLength.ShouldBe(16u);
        header.IsCurrent(220_000).ShouldBeTrue();
        SharedHasher().VerifyHashedPassword(User, hash, Password).ShouldBe(PasswordVerificationResult.Success);
    }

    [Fact]
    public void The_iteration_count_is_the_owasp_value_for_pbkdf2_hmac_sha512()
    {
        IdentityModuleInstaller.PasswordHashIterations.ShouldBe(220_000);
    }

    [Fact]
    public void Hashes_with_the_old_100000_iterations_still_verify_and_ask_for_a_rehash()
    {
        var old = new PasswordHasher<AppUser>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
            .HashPassword(User, Password);
        IdentityV3HashHeader.TryParse(old, out var header).ShouldBeTrue();
        header.IterationCount.ShouldBe(100_000u);
        header.IsCurrent(220_000).ShouldBeFalse();

        SharedHasher().VerifyHashedPassword(User, old, Password).ShouldBe(PasswordVerificationResult.SuccessRehashNeeded);
    }

    [Fact]
    public void A_random_v3_shaped_placeholder_verifies_no_password()
    {
        // The T001 placeholder: valid header (0x01, HMAC-SHA512, 220,000, salt length 16), random salt and subkey.
        byte[] header = [0x01, 0, 0, 0, 2, 0, 0x03, 0x5B, 0x60, 0, 0, 0, 16];
        var placeholder = Convert.ToBase64String([.. header, .. System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)]);

        IdentityV3HashHeader.TryParse(placeholder, out var parsed).ShouldBeTrue();
        parsed.IsCurrent(220_000).ShouldBeTrue();
        SharedHasher().VerifyHashedPassword(User, placeholder, Password).ShouldBe(PasswordVerificationResult.Failed);
        SharedHasher().VerifyHashedPassword(User, placeholder, string.Empty).ShouldBe(PasswordVerificationResult.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("AQAAAA==")]                       // too short for a header
    [InlineData("AAAAAAIAA1tgAAAAEAAAAAAAAAAAAAAA")] // marker 0x00 (Identity V2)
    public void Anything_but_a_v3_header_is_rejected(string? hash)
    {
        IdentityV3HashHeader.TryParse(hash, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_host_registration_uses_the_shared_configuration()
    {
        var services = new ServiceCollection();
        new IdentityModuleInstaller().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<PasswordHasherOptions>>().Value.IterationCount.ShouldBe(220_000);
        var hash = provider.GetRequiredService<IPasswordHasher<AppUser>>().HashPassword(User, Password);
        IdentityV3HashHeader.TryParse(hash, out var header).ShouldBeTrue();
        header.IterationCount.ShouldBe(220_000u);
    }
}
