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
        EmailAddress.Create("Tomas@Dealer.Example").Value.ShouldBe(EmailAddress.Create("tomas@dealer.example").Value);
    }

    [Fact]
    public void Values_from_the_database_must_be_valid()
    {
        UserName.FromDatabase("tomas.r").Value.ShouldBe("tomas.r");
        Should.Throw<InvalidOperationException>(() => UserName.FromDatabase("x"));
        Should.Throw<InvalidOperationException>(() => EmailAddress.FromDatabase("no-at-sign"));
    }
}
