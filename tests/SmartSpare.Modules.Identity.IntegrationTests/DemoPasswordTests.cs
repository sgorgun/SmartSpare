using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartSpare.MigrationRunner;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Security;

namespace SmartSpare.Modules.Identity;

/// <summary>Issue #7: T001 stores placeholders only; set-demo-passwords stores real hashes at load time.</summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class DemoPasswordTests(IdentityDatabaseFixture database)
{
    private const string DemoPassword = "a password chosen in .env";

    // The demo users of db/testdata/T001 with the same placeholder expression as T001 (keep both in sync).
    private static readonly string InsertDemoUsers =
        "INSERT INTO iam.employee (id, code, full_name, position_id) "
        + "SELECT format('00000000-0000-4000-8000-%s', lpad(n::text, 12, '0'))::uuid, 'E' || lpad(n::text, 4, '0'), 'Demo User', p.id "
        + "FROM generate_series(1, 12) AS n, iam.position p WHERE p.code = 'INVENTORY_PLANNER'; "
        + "INSERT INTO iam.app_user (id, employee_id, user_name, email, full_name, password_hash) "
        + "SELECT format('00000000-0000-4000-8000-%s', lpad(n::text, 12, '0'))::uuid, format('00000000-0000-4000-8000-%s', lpad(n::text, 12, '0'))::uuid, "
        + "'demo.user' || n, 'demo.user' || n || '@dealer.example', 'Demo User', "
        + "translate(encode('\\x010000000200035b6000000010'::bytea || gen_random_bytes(48), 'base64'), E'\\n', '') "
        + "FROM generate_series(1, 12) AS n";

    [Fact]
    public async Task Demo_users_get_hashes_that_the_application_verifies()
    {
        // Without T001 there is nothing to update: the command fails instead of silently doing nothing.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            DemoPasswords.SetAsync(database.MigratorConnectionString, DemoPassword, TestContext.Current.CancellationToken));

        await database.ExecuteAsync(InsertDemoUsers);
        var hasher = SharedHasher();
        var placeholders = await LoadDemoUsersAsync();
        placeholders.Count.ShouldBe(12);
        foreach (var user in placeholders)
        {
            // Accepted by ck_app_user_password_hash_format, a current V3 header, but no password verifies.
            IdentityV3HashHeader.TryParse(user.PasswordHash, out var header).ShouldBeTrue();
            header.IsCurrent(220_000).ShouldBeTrue();
            hasher.VerifyHashedPassword(user, user.PasswordHash!, DemoPassword).ShouldBe(PasswordVerificationResult.Failed);
        }

        // T001 ends by anonymising one former employee (user 12); that user must keep a NULL hash.
        await database.ExecuteAsync(
            "UPDATE iam.app_user SET password_hash = NULL, is_active = false, anonymised_at = now() "
            + "WHERE id = '00000000-0000-4000-8000-000000000012'");

        var updated = await DemoPasswords.SetAsync(database.MigratorConnectionString, DemoPassword, TestContext.Current.CancellationToken);

        updated.ShouldBe(11);
        var all = await LoadDemoUsersAsync();
        var anonymised = all.Single(u => u.AnonymisedAt is not null);
        anonymised.PasswordHash.ShouldBeNull();
        var users = all.Where(u => u.AnonymisedAt is null).ToList();
        users.Count.ShouldBe(11);
        foreach (var user in users)
        {
            hasher.VerifyHashedPassword(user, user.PasswordHash!, DemoPassword).ShouldBe(PasswordVerificationResult.Success);
            IdentityV3HashHeader.TryParse(user.PasswordHash, out var header).ShouldBeTrue();
            header.IterationCount.ShouldBe(220_000u);
            user.SecurityStamp.ShouldNotBe(placeholders.Single(p => p.Id == user.Id).SecurityStamp);
        }

        users.Select(u => u.PasswordHash).Distinct().Count().ShouldBe(11, "every user gets their own salt");
    }

    private async Task<List<AppUser>> LoadDemoUsersAsync()
    {
        await using var context = database.CreateContext();
        var ids = DemoPasswords.DemoUserIds.ToList();
        return await context.Set<AppUser>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static PasswordHasher<AppUser> SharedHasher()
    {
        var options = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(options);
        return new PasswordHasher<AppUser>(Options.Create(options));
    }
}
