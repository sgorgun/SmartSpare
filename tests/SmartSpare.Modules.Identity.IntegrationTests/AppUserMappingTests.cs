using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;

namespace SmartSpare.Modules.Identity;

/// <summary>The aggregate is mapped directly onto the unchanged iam tables (ADR 0013), with xmin as concurrency token.</summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class AppUserMappingTests(IdentityDatabaseFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);

    [Fact]
    public async Task An_account_of_an_employee_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        await using (var context = database.CreateContext())
        {
            context.Add(await EmployeeAsync(context, id));
            context.Add(NewUser(id));
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var user = await LoadAsync(context, id);
            user.UserName.Value.ShouldBe($"u{id:N}"[..20]);
            user.EmployeeId.ShouldBe(id);
            user.IsActive.ShouldBeTrue();
            user.CreatedAt.ShouldNotBe(default);

            user.RecordFailedSignIn();
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            (await LoadAsync(context, id)).FailedLoginCount.ShouldBe((short)1);
        }
    }

    [Fact]
    public async Task Concurrent_changes_to_the_same_user_are_detected_through_xmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using (var context = database.CreateContext())
        {
            context.Add(await EmployeeAsync(context, id));
            context.Add(NewUser(id));
            await context.SaveChangesAsync(ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await LoadAsync(first, id);
        var b = await LoadAsync(second, id);

        a.RecordFailedSignIn();
        await first.SaveChangesAsync(ct);

        b.RecordSuccessfulSignIn(Now);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task A_mixed_case_user_name_is_stored_in_lower_case_and_found_case_insensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var mixed = $"Mixed.{id:N}"[..20];
        await using (var context = database.CreateContext())
        {
            context.Add(await EmployeeAsync(context, id));
            context.Add(AppUser.Create(
                id, id, UserName.Create(mixed).Value, Hash));
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var stored = await context.Database
                .SqlQuery<string>($"SELECT user_name::text AS \"Value\" FROM iam.app_user WHERE id = {id}")
                .SingleAsync(ct);
            stored.ShouldBe(mixed.ToLowerInvariant());

            // citext lookup: the upper-case spelling finds the same user.
            var upper = UserName.Create(mixed.ToUpperInvariant()).Value;
            (await context.Set<AppUser>().SingleAsync(u => u.UserName == upper, ct)).Id.ShouldBe(id);
        }
    }

    private static AppUser NewUser(Guid id) => AppUser.Create(
        id, id, UserName.Create($"u{id:N}"[..20]).Value, Hash);

    /// <summary>The employee every account belongs to (ADR 0026), with the same id as the account.</summary>
    private static async Task<Employee> EmployeeAsync(DbContext context, Guid id)
    {
        var planner = await context.Set<Position>().Where(p => p.Code == "INVENTORY_PLANNER").Select(p => p.Id).SingleAsync(TestContext.Current.CancellationToken);
        var code = "E" + (BitConverter.ToUInt32(id.ToByteArray(), 0) % 900000 + 100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Employee.Create(id, code, new EmployeeDetails("Mapping Test", null, null, planner, null)).Value;
    }

    private static Task<AppUser> LoadAsync(DbContext context, Guid id) =>
        context.Set<AppUser>().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);
}
