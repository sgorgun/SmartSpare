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
        Guid.NewGuid(), UserName.Create("rasa.petrauskiene").Value, EmailAddress.Create("rasa@dealer.example").Value, "Rasa Petrauskienė", null, Hash);

    [Fact]
    public void Applying_access_grants_and_revokes_exactly_the_difference()
    {
        var user = NewUser();
        user.AssignRole(2, Now, null);
        user.AssignRole(5, Now, null);
        user.AssignBrand(1);

        AccessResolver.Apply(user, new AccessResolver.Resolved([5, 7], [3], HomeWarehouseId: 4), Now, grantedBy: Guid.NewGuid());

        user.Roles.Select(r => r.RoleId).ShouldBe(new short[] { 5, 7 }, ignoreOrder: true);
        user.Brands.Select(b => b.BrandId).ShouldBe([(short)3]);
        user.HomeWarehouseId.ShouldBe((short)4);
    }

    [Fact]
    public void Applying_the_same_access_keeps_the_sessions()
    {
        var user = NewUser();
        AccessResolver.Apply(user, new AccessResolver.Resolved([5], [1], 3), Now, null);
        var stamp = user.SecurityStamp;

        AccessResolver.Apply(user, new AccessResolver.Resolved([5], [1], 3), Now, null);

        user.SecurityStamp.ShouldBe(stamp, "nothing changed, so no session has to re-check its claims");
    }

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
    public void A_valid_new_user_passes()
    {
        var request = new CreateUserRequest("Rasa.P", "rasa@dealer.example", "Rasa Petrauskienė", null, "a long initial password",
            new UserAccessRequest([AppRoles.WarehouseKeeper], [], 3));

        new CreateUserRequestValidator().TestValidate(request).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ab", "rasa@dealer.example", "a long initial password", "UserName")]
    [InlineData("rasa p", "rasa@dealer.example", "a long initial password", "UserName")]
    [InlineData("rasa.p", "not-an-address", "a long initial password", "Email")]
    [InlineData("rasa.p", "rasa@dealer.example", "too short", "Password")]
    [InlineData("rasa.p", "rasa@dealer.example", "aaaaaaaaaaaaaaab", "Password")]
    public void Invalid_new_users_fail_on_their_field(string userName, string email, string password, string field) =>
        new CreateUserRequestValidator().TestValidate(
                new CreateUserRequest(userName, email, "Rasa", null, password, new UserAccessRequest([AppRoles.Sponsor], [], null)))
            .Errors.ShouldContain(e => e.PropertyName.StartsWith(field, StringComparison.Ordinal));

    [Fact]
    public void Access_needs_a_known_role_and_brands_only_for_brand_managers()
    {
        var validator = new UserAccessRequestValidator();

        validator.TestValidate(new UserAccessRequest([], [], null)).ShouldHaveValidationErrorFor(r => r.Roles);
        validator.TestValidate(new UserAccessRequest(["SUPERUSER"], [], null)).ShouldHaveValidationErrorFor(r => r.Roles);
        validator.TestValidate(new UserAccessRequest([AppRoles.InventoryPlanner], ["JD"], null)).ShouldHaveValidationErrorFor(r => r.Brands);
        validator.TestValidate(new UserAccessRequest([AppRoles.BrandManager], ["JD"], null)).IsValid.ShouldBeTrue();
        validator.TestValidate(new UserAccessRequest([AppRoles.WarehouseKeeper], [], 0)).ShouldHaveValidationErrorFor(r => r.HomeWarehouseId);
    }

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
        new UpdateUserAccessCommandValidator().TestValidate(new UpdateUserAccessCommand(Guid.NewGuid(), new UserAccessRequest([], [], null))).IsValid.ShouldBeFalse();
        new ListUsersQueryValidator().TestValidate(new ListUsersQuery(null, 0, 25)).IsValid.ShouldBeFalse();
        new ListUsersQueryValidator().TestValidate(new ListUsersQuery("rasa", 1, 25)).IsValid.ShouldBeTrue();
    }
}
