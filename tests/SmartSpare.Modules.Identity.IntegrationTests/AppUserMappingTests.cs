using Microsoft.EntityFrameworkCore;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

/// <summary>The aggregate is mapped directly onto the unchanged iam tables (ADR 0013), with xmin as concurrency token.</summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class AppUserMappingTests(IdentityDatabaseFixture database)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = "AQAAAAIAA1tgAAAAE" + new string('A', 67);

    [Fact]
    public async Task A_user_with_roles_and_brands_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        short brandManager, sponsor;
        short[] brands;

        await using (var context = database.CreateContext())
        {
            brandManager = await RoleIdAsync(context, "BRAND_MANAGER");
            sponsor = await RoleIdAsync(context, "SPONSOR");
            brands = await context.Database.SqlQuery<short>($"SELECT id AS \"Value\" FROM ref.brand ORDER BY id LIMIT 2").ToArrayAsync(ct);

            var user = NewUser(id);
            user.AssignRole(brandManager, Now, null);
            user.AssignRole(sponsor, Now, null);
            user.AssignBrand(brands[0]);
            user.AssignBrand(brands[1]);
            context.Add(user);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var user = await LoadAsync(context, id);
            user.UserName.Value.ShouldBe($"u{id:N}"[..20]);
            user.Email.Value.ShouldBe($"{id:N}@dealer.example");
            user.IsActive.ShouldBeTrue();
            user.CreatedAt.ShouldNotBe(default);
            user.Roles.Select(r => r.RoleId).ShouldBe([brandManager, sponsor], ignoreOrder: true);
            user.Brands.Select(b => b.BrandId).ShouldBe(brands, ignoreOrder: true);

            user.RevokeRole(sponsor);
            user.RemoveBrand(brands[1]);
            user.RecordFailedSignIn(Now, LockoutPolicy.Default);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = database.CreateContext())
        {
            var user = await LoadAsync(context, id);
            user.Roles.ShouldHaveSingleItem().RoleId.ShouldBe(brandManager);
            user.Brands.ShouldHaveSingleItem().BrandId.ShouldBe(brands[0]);
            user.FailedLoginCount.ShouldBe((short)1);
        }
    }

    [Fact]
    public async Task Concurrent_changes_to_the_same_user_are_detected_through_xmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        await using (var context = database.CreateContext())
        {
            context.Add(NewUser(id));
            await context.SaveChangesAsync(ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await LoadAsync(first, id);
        var b = await LoadAsync(second, id);

        a.RecordFailedSignIn(Now, LockoutPolicy.Default);
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
            context.Add(AppUser.Create(
                id, UserName.Create(mixed).Value, EmailAddress.Create($"{id:N}@dealer.example").Value, "Mapping Test", null, Hash));
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
        id, UserName.Create($"u{id:N}"[..20]).Value, EmailAddress.Create($"{id:N}@dealer.example").Value, "Mapping Test", null, Hash);

    private static Task<AppUser> LoadAsync(DbContext context, Guid id) =>
        context.Set<AppUser>().Include(u => u.Roles).Include(u => u.Brands).SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);

    private static Task<short> RoleIdAsync(DbContext context, string code) =>
        context.Set<Role>().Where(r => r.Code == code).Select(r => r.Id).SingleAsync(TestContext.Current.CancellationToken);
}
