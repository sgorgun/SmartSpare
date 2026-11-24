using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

public sealed class AppUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    // A V3-shaped hash: the format the CHECK and the aggregate accept (no password verifies against it).
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);

    private static AppUser NewUser() => AppUser.Create(
        Guid.NewGuid(),
        UserName.Create("tomas.radzevicius").Value,
        EmailAddress.Create("tomas.radzevicius@dealer.example").Value,
        "Tomas Radzevičius",
        "+370 612 10001",
        Hash);

    [Fact]
    public void A_new_user_is_active_with_a_password_and_no_failures()
    {
        var user = NewUser();

        user.IsActive.ShouldBeTrue();
        user.PasswordHash.ShouldBe(Hash);
        user.FailedLoginCount.ShouldBe((short)0);
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
        user.IsLockedOut(Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Secret123!")]                                // plain text
    [InlineData("0d107d09f5bbe40cade3de5c71e9e9b7")]          // MD5 digest
    [InlineData("")]
    public void Only_identity_v3_hashes_are_accepted(string hash)
    {
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.NewGuid(), UserName.Create("abc").Value, EmailAddress.Create("a@b.c").Value, "A B", null, hash));
        Should.Throw<ArgumentException>(() => NewUser().ChangePasswordHash(hash));
    }

    [Fact]
    public void A_user_needs_an_identifier_and_a_name()
    {
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.Empty, UserName.Create("abc").Value, EmailAddress.Create("a@b.c").Value, "A B", null, Hash));
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.NewGuid(), UserName.Create("abc").Value, EmailAddress.Create("a@b.c").Value, " ", null, Hash));
    }

    [Fact]
    public void A_successful_sign_in_records_the_time_and_clears_failures_and_lockout()
    {
        var user = NewUser();
        user.RecordFailedSignIn(Now, LockoutPolicy.Default);
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedSignIn(Now, LockoutPolicy.Default);
        }

        user.IsLockedOut(Now).ShouldBeTrue();

        user.RecordSuccessfulSignIn(Now.AddMinutes(20)).IsSuccess.ShouldBeTrue();

        user.LastLoginAt.ShouldBe(Now.AddMinutes(20));
        user.FailedLoginCount.ShouldBe((short)0);
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public void Failures_below_the_limit_only_count()
    {
        var user = NewUser();

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedSignIn(Now, LockoutPolicy.Default).ShouldBeFalse();
        }

        user.FailedLoginCount.ShouldBe((short)4);
        user.IsLockedOut(Now).ShouldBeFalse();
    }

    [Fact]
    public void The_fifth_failure_locks_the_account_for_fifteen_minutes_and_resets_the_counter()
    {
        var user = NewUser();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedSignIn(Now, LockoutPolicy.Default);
        }

        user.RecordFailedSignIn(Now, LockoutPolicy.Default).ShouldBeTrue();

        user.LockoutEnd.ShouldBe(Now.AddMinutes(15));
        user.FailedLoginCount.ShouldBe((short)0);
        user.IsLockedOut(Now.AddMinutes(14)).ShouldBeTrue();
        user.IsLockedOut(Now.AddMinutes(15)).ShouldBeFalse();
    }

    [Fact]
    public void The_failure_counter_never_exceeds_the_database_limit()
    {
        var user = NewUser();
        var policy = new LockoutPolicy(AppUser.MaxFailedLoginCount, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 1000; i++)
        {
            user.RecordFailedSignIn(Now, policy);
            user.FailedLoginCount.ShouldBeInRange((short)0, AppUser.MaxFailedLoginCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void A_lockout_policy_outside_the_database_limit_is_rejected(int maxFailedAttempts)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            NewUser().RecordFailedSignIn(Now, new LockoutPolicy(maxFailedAttempts, TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void Deactivation_rotates_the_stamp_raises_an_event_and_blocks_sign_in()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.Deactivate(Now);

        user.IsActive.ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(stamp);
        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new UserDeactivated(user.Id, Now));
        user.RecordSuccessfulSignIn(Now).Error.ShouldBe(IdentityErrors.UserInactive);
    }

    [Fact]
    public void Deactivating_twice_changes_nothing_the_second_time()
    {
        var user = NewUser();
        user.Deactivate(Now);
        var stamp = user.SecurityStamp;

        user.Deactivate(Now.AddMinutes(1));

        user.SecurityStamp.ShouldBe(stamp);
        user.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void A_new_password_hash_rotates_the_stamp()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;
        var newHash = "AQAAAAIAA1tgAAAAE" + new string('B', 67);

        user.ChangePasswordHash(newHash);

        user.PasswordHash.ShouldBe(newHash);
        user.SecurityStamp.ShouldNotBe(stamp);
    }

    [Fact]
    public void Role_changes_rotate_the_stamp_and_repeats_change_nothing()
    {
        var user = NewUser();
        var grantedBy = Guid.NewGuid();
        var stamp = user.SecurityStamp;

        user.AssignRole(1, Now, grantedBy);
        user.SecurityStamp.ShouldNotBe(stamp);
        var role = user.Roles.ShouldHaveSingleItem();
        role.RoleId.ShouldBe((short)1);
        role.GrantedAt.ShouldBe(Now);
        role.GrantedBy.ShouldBe(grantedBy);

        stamp = user.SecurityStamp;
        user.AssignRole(1, Now.AddDays(1), null);
        user.SecurityStamp.ShouldBe(stamp);
        user.Roles.Count.ShouldBe(1);

        user.RevokeRole(1);
        user.Roles.ShouldBeEmpty();
        user.SecurityStamp.ShouldNotBe(stamp);

        stamp = user.SecurityStamp;
        user.RevokeRole(1);
        user.SecurityStamp.ShouldBe(stamp);
    }

    [Fact]
    public void Brand_changes_rotate_the_stamp_and_repeats_change_nothing()
    {
        var user = NewUser();
        var stamp = user.SecurityStamp;

        user.AssignBrand(3);
        user.Brands.ShouldHaveSingleItem().BrandId.ShouldBe((short)3);
        user.SecurityStamp.ShouldNotBe(stamp);

        stamp = user.SecurityStamp;
        user.AssignBrand(3);
        user.SecurityStamp.ShouldBe(stamp);

        user.RemoveBrand(3);
        user.Brands.ShouldBeEmpty();
        user.SecurityStamp.ShouldNotBe(stamp);

        stamp = user.SecurityStamp;
        user.RemoveBrand(3);
        user.SecurityStamp.ShouldBe(stamp);
    }
}
