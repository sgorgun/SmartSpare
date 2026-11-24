using Npgsql;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// The value objects accept exactly what the CHECK constraints accept: the shared cases of the unit tests are
/// inserted into <c>iam.app_user</c> (each in a transaction that is rolled back). An accepted user name is
/// inserted in the canonical form the value object stores; a rejected one is inserted as given.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ValueObjectParityTests(IdentityDatabaseFixture database)
{
    [Theory]
    [MemberData(nameof(ValueObjectCases.UserNames), MemberType = typeof(ValueObjectCases))]
    public async Task UserName_cases_match_ck_app_user_user_name(string value, bool valid)
    {
        var stored = UserName.Create(value) is { IsSuccess: true } userName ? userName.Value.Value : value;
        stored.ShouldBe(valid ? value.ToLowerInvariant() : value);

        var accepted = await InsertAcceptedAsync(stored, "parity@dealer.example", "ck_app_user_user_name");
        accepted.ShouldBe(valid);
    }

    [Theory]
    [MemberData(nameof(ValueObjectCases.EmailAddresses), MemberType = typeof(ValueObjectCases))]
    public async Task EmailAddress_cases_match_ck_app_user_email(string value, bool valid)
    {
        var accepted = await InsertAcceptedAsync("parity.user", value, "ck_app_user_email");
        accepted.ShouldBe(valid);
    }

    private async Task<bool> InsertAcceptedAsync(string userName, string email, string constraint)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(database.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO iam.app_user (user_name, email, full_name, password_hash) VALUES (@u, @e, 'Parity Test', @h)",
            connection, transaction);
        insert.Parameters.AddWithValue("u", userName);
        insert.Parameters.AddWithValue("e", email);
        insert.Parameters.AddWithValue("h", "AQAAAAIAA1tgAAAAE" + new string('A', 67));
        try
        {
            await insert.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == constraint)
        {
            return false;
        }
        finally
        {
            await transaction.RollbackAsync(ct);
        }
    }
}
