using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

public sealed class AppUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    // A V3-shaped hash: the format the CHECK and the aggregate accept (no password verifies against it).
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);

    private static AppUser NewUser() => AppUser.Create(
        Guid.NewGuid(), Guid.NewGuid(),
        UserName.Create("tomas.radzevicius").Value,
        Hash);

    [Fact]
    public void A_new_user_is_active_with_a_password_and_no_failures()
    {
        var user = NewUser();

        user.IsActive.ShouldBeTrue();
        user.PasswordHash.ShouldBe(Hash);
        user.FailedLoginCount.ShouldBe((short)0);
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
        user.LockoutEnd.ShouldBeNull();
    }

    [Theory]
    [InlineData("Secret123!")]                                // plain text
    [InlineData("0d107d09f5bbe40cade3de5c71e9e9b7")]          // MD5 digest
    [InlineData("")]
    public void Only_identity_v3_hashes_are_accepted(string hash)
    {
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.NewGuid(), Guid.NewGuid(), UserName.Create("abc").Value, hash));
        Should.Throw<ArgumentException>(() => NewUser().ChangePasswordHash(hash));
    }

    [Fact]
    public void A_user_needs_an_identifier_and_an_employee()
    {
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.Empty, Guid.NewGuid(), UserName.Create("abc").Value, Hash));
        Should.Throw<ArgumentException>(() => AppUser.Create(
            Guid.NewGuid(), Guid.Empty, UserName.Create("abc").Value, Hash), "an account belongs to an employee (ADR 0026)");
    }

    [Fact]
    public void A_successful_sign_in_records_the_time()
    {
        var user = NewUser();

        user.RecordSuccessfulSignIn(Now).IsSuccess.ShouldBeTrue();

        user.LastLoginAt.ShouldBe(Now);
    }

    [Fact]
    public void A_successful_sign_in_leaves_the_counter_to_identity()
    {
        // ASP.NET Core Identity owns the lockout algorithm and resets the counter itself (ResetFailedSignIns).
        var user = NewUser();
        user.RecordFailedSignIn();

        user.RecordSuccessfulSignIn(Now);

        user.FailedLoginCount.ShouldBe((short)1);
    }

    [Fact]
    public void Failed_sign_ins_are_counted_one_by_one()
    {
        var user = NewUser();

        user.RecordFailedSignIn().ShouldBe((short)1);
        user.RecordFailedSignIn().ShouldBe((short)2);

        user.FailedLoginCount.ShouldBe((short)2);
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public void Resetting_clears_the_counter()
    {
        var user = NewUser();
        user.RecordFailedSignIn();
        user.RecordFailedSignIn();

        user.ResetFailedSignIns();

        user.FailedLoginCount.ShouldBe((short)0);
    }

    [Fact]
    public void A_lockout_end_locks_and_null_unlocks()
    {
        var user = NewUser();

        user.SetLockoutEnd(Now.AddMinutes(15));
        user.LockoutEnd.ShouldBe(Now.AddMinutes(15));

        user.SetLockoutEnd(null);
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public void The_failure_counter_never_exceeds_the_database_limit()
    {
        var user = NewUser();
        for (var i = 0; i < AppUser.MaxFailedLoginCount; i++)
        {
            user.RecordFailedSignIn();
        }

        Should.Throw<InvalidOperationException>(() => user.RecordFailedSignIn());
        user.FailedLoginCount.ShouldBe(AppUser.MaxFailedLoginCount);
    }

    [Fact]
    public void The_default_lockout_policy_is_five_attempts_for_fifteen_minutes()
    {
        LockoutPolicy.Default.MaxFailedAttempts.ShouldBe(5);
        LockoutPolicy.Default.Duration.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(101, 15)]   // above the CHECK's limit of failed_login_count
    [InlineData(5, 0)]
    public void A_lockout_policy_outside_the_limits_is_rejected(int maxFailedAttempts, int minutes)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LockoutPolicy(maxFailedAttempts, TimeSpan.FromMinutes(minutes)));
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

}
