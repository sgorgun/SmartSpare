using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartSpare.Modules.Identity;

namespace SmartSpare.MigrationRunner;

/// <summary>
/// <c>set-demo-passwords</c>: gives the synthetic demo users of <c>db/testdata/T001</c> a real password hash
/// computed now from <c>SMARTSPARE_DEMO_USER_PASSWORD</c> (issue #7). T001 itself contains no hashes: it stores a
/// random placeholder in the V3 format that no password verifies against. Hashing uses the application's own
/// configuration (<see cref="IdentityModuleInstaller.ConfigurePasswordHasher"/>).
/// </summary>
public static class DemoPasswords
{
    public const string PasswordVariable = "SMARTSPARE_DEMO_USER_PASSWORD";

    /// <summary>The 12 demo users of T001 (fixed ids <c>00000000-0000-4000-8000-000000000001</c> … <c>…012</c>).</summary>
    public static IReadOnlyList<Guid> DemoUserIds { get; } =
        [.. Enumerable.Range(1, 12).Select(n => Guid.Parse($"00000000-0000-4000-8000-{n:D12}"))];

    /// <summary>
    /// Hashes <paramref name="password"/> once per demo user (own salt) and stores it; returns the number of users updated.
    /// Anonymised users (T001 anonymises one former employee, GDPR Art. 17) keep their NULL hash and are skipped.
    /// </summary>
    public static async Task<int> SetAsync(string migratorConnectionString, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migratorConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var options = new PasswordHasherOptions();
        IdentityModuleInstaller.ConfigurePasswordHasher(options);
        var hasher = new PasswordHasher<DemoUser>(Options.Create(options));

        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The audit trigger records why the rows changed (password_hash itself is excluded from the audit).
        await using (var reason = new NpgsqlCommand("SELECT set_config('app.reason', 'set-demo-passwords', true)", connection, transaction))
        {
            await reason.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var anonymised = new HashSet<Guid>();
        var found = 0;
        await using (var select = new NpgsqlCommand(
            "SELECT id, anonymised_at IS NOT NULL FROM iam.app_user WHERE id = ANY(@ids)", connection, transaction))
        {
            select.Parameters.AddWithValue("ids", DemoUserIds.ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found++;
                if (reader.GetBoolean(1))
                {
                    anonymised.Add(reader.GetGuid(0));
                }
            }
        }

        if (found != DemoUserIds.Count)
        {
            throw new InvalidOperationException(
                $"Only {found} of {DemoUserIds.Count} demo users exist: load db/testdata/T001__demo_dataset.sql before set-demo-passwords.");
        }

        var updated = 0;
        foreach (var id in DemoUserIds.Where(id => !anonymised.Contains(id)))
        {
            await using var update = new NpgsqlCommand(
                "UPDATE iam.app_user SET password_hash = @hash, security_stamp = gen_random_uuid() WHERE id = @id AND anonymised_at IS NULL",
                connection, transaction);
            update.Parameters.AddWithValue("hash", hasher.HashPassword(DemoUser.Instance, password));
            update.Parameters.AddWithValue("id", id);
            updated += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>The hasher's user type parameter is not used by the V3 algorithm.</summary>
    private sealed class DemoUser
    {
        public static readonly DemoUser Instance = new();
    }
}
