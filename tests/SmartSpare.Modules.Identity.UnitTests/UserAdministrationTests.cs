using FluentValidation.TestHelper;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Features.Users;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// The rules of user administration (#46) that need no database: the request validators shared with the web client,
/// the role, brand and hub changes applied to a user, and the stamp rotation they cause.
/// </summary>
public sealed class UserAdministrationTests
{
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    private static AppUser NewUser() => AppUser.Create(
        Guid.NewGuid(), Guid.NewGuid(), UserName.Create("rasa.petrauskiene").Value, EmailAddress.Create("rasa@dealer.example").Value, "Rasa Petrauskienė", null, Hash);

    [Fact]
    public void A_new_home_hub_rotates_the_stamp_because_it_is_a_claim()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.SetHomeWarehouse(3);
        user.SecurityStamp.ShouldNotBe(stamp);

        var after = user.SecurityStamp;
        user.SetHomeWarehouse(3);
        user.SecurityStamp.ShouldBe(after);
    }

    [Fact]
    public void Unlocking_ends_the_lockout_and_resets_the_counter()
    {
        var user = NewUser();
        user.RecordFailedSignIn();
        user.SetLockoutEnd(Now.AddMinutes(15));

        user.Unlock();

        user.LockoutEnd.ShouldBeNull();
        user.FailedLoginCount.ShouldBe((short)0);
    }

    [Fact]
    public void A_valid_new_account_passes()
    {
        new CreateAccountRequestValidator().TestValidate(new CreateAccountRequest("Rasa.P", "a long initial password")).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ab", "a long initial password", "UserName")]
    [InlineData("rasa p", "a long initial password", "UserName")]
    [InlineData("rasa.p", "too short", "Password")]
    [InlineData("rasa.p", "aaaaaaaaaaaaaaab", "Password")]
    public void Invalid_new_accounts_fail_on_their_field(string userName, string password, string field) =>
        new CreateAccountRequestValidator().TestValidate(new CreateAccountRequest(userName, password))
            .Errors.ShouldContain(e => e.PropertyName.StartsWith(field, StringComparison.Ordinal));

    [Fact]
    public void A_new_password_differs_from_the_current_one()
    {
        var validator = new ChangePasswordRequestValidator();

        validator.TestValidate(new ChangePasswordRequest("the same long phrase", "the same long phrase")).ShouldHaveValidationErrorFor(r => r.NewPassword);
        validator.TestValidate(new ChangePasswordRequest("old phrase", "a brand new passphrase")).IsValid.ShouldBeTrue();
        new ChangeOwnPasswordCommandValidator().TestValidate(new ChangeOwnPasswordCommand(new ChangePasswordRequest("", "x"))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_command_validators_apply_the_request_rules()
    {
        new ListUsersQueryValidator().TestValidate(new ListUsersQuery(null, 0, 25)).IsValid.ShouldBeFalse();
        new ListUsersQueryValidator().TestValidate(new ListUsersQuery("rasa", 1, 25)).IsValid.ShouldBeTrue();
    }
}
