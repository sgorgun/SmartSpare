using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

public sealed class ValueObjectTests
{
    [Theory]
    [MemberData(nameof(ValueObjectCases.UserNames), MemberType = typeof(ValueObjectCases))]
    public void UserName_follows_the_database_check(string value, bool valid)
    {
        var result = UserName.Create(value);

        result.IsSuccess.ShouldBe(valid);
        if (!valid)
        {
            result.Error.ShouldBe(IdentityErrors.InvalidUserName);
        }
    }

    [Theory]
    [MemberData(nameof(ValueObjectCases.EmailAddresses), MemberType = typeof(ValueObjectCases))]
    public void EmailAddress_follows_the_database_check(string value, bool valid)
    {
        var result = EmailAddress.Create(value);

        result.IsSuccess.ShouldBe(valid);
        if (!valid)
        {
            result.Error.ShouldBe(IdentityErrors.InvalidEmailAddress);
        }
    }

    [Fact]
    public void Null_is_rejected()
    {
        UserName.Create(null).IsFailure.ShouldBeTrue();
        EmailAddress.Create(null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Values_compare_case_insensitively_like_citext()
    {
        UserName.Create("Tomas.R").Value.ShouldBe(UserName.Create("tomas.r").Value);
        UserName.Create("Tomas.R").Value.GetHashCode().ShouldBe(UserName.Create("TOMAS.R").Value.GetHashCode());
        EmailAddress.Create("Tomas@Dealer.Example").Value.ShouldBe(EmailAddress.Create("tomas@dealer.example").Value);
    }

    [Theory]
    [InlineData("Tomas.Radzevicius", "tomas.radzevicius")]
    [InlineData("ADMIN.SMARTSPARE", "admin.smartspare")]
    [InlineData("tomas.r", "tomas.r")]
    [InlineData("A_B-C.9", "a_b-c.9")]
    public void UserName_is_stored_in_lower_case(string input, string canonical)
    {
        var userName = UserName.Create(input).Value;

        userName.Value.ShouldBe(canonical);
        userName.ToString().ShouldBe(canonical);
    }

    [Fact]
    public void UserName_from_the_database_is_canonical_too()
    {
        UserName.FromDatabase("Tomas.R").Value.ShouldBe("tomas.r");
    }

    [Fact]
    public void UserName_rejects_non_ascii_case_variants()
    {
        // U+212A KELVIN SIGN lower-cases to 'k' but is not an ASCII letter.
        UserName.Create("Karolis").IsFailure.ShouldBeTrue();
        UserName.Create("İsmail").IsFailure.ShouldBeTrue(); // capital I with dot above
    }

    [Fact]
    public void Values_from_the_database_must_be_valid()
    {
        UserName.FromDatabase("tomas.r").Value.ShouldBe("tomas.r");
        Should.Throw<InvalidOperationException>(() => UserName.FromDatabase("x"));
        Should.Throw<InvalidOperationException>(() => EmailAddress.FromDatabase("no-at-sign"));
    }
}
