using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Persistence.Platform;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.UnitTests;

public sealed class DatabaseErrorMapperTests
{
    private static PostgresException Pg(string sqlState, string message = "message from the database") =>
        new(message, "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("SS100", ErrorType.BusinessRule)]
    [InlineData("SS103", ErrorType.BusinessRule)]
    [InlineData("SS104", ErrorType.BusinessRule)]
    [InlineData("SS200", ErrorType.Conflict)]
    [InlineData("SS201", ErrorType.Conflict)]
    [InlineData("SS300", ErrorType.Conflict)]
    [InlineData("SS301", ErrorType.Forbidden)]
    [InlineData("SS302", ErrorType.Forbidden)]
    [InlineData("SS400", ErrorType.BusinessRule)]
    [InlineData("SS402", ErrorType.BusinessRule)]
    [InlineData("SS500", ErrorType.BusinessRule)]
    [InlineData("SS501", ErrorType.Conflict)]
    public void BusinessRuleStates_KeepTheirCodeAndMessage(string sqlState, ErrorType expected)
    {
        var error = DatabaseErrorMapper.ToError(Pg(sqlState, "Insufficient stock of part 7"));

        error.ShouldNotBeNull();
        error.Code.ShouldBe(sqlState);
        error.Type.ShouldBe(expected);
        error.Description.ShouldBe("Insufficient stock of part 7");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "Database.UniqueViolation", ErrorType.Conflict)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "Database.ForeignKeyViolation", ErrorType.BusinessRule)]
    [InlineData(PostgresErrorCodes.CheckViolation, "Database.CheckViolation", ErrorType.BusinessRule)]
    [InlineData(PostgresErrorCodes.ExclusionViolation, "Database.ExclusionViolation", ErrorType.BusinessRule)]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, "Database.InsufficientPrivilege", ErrorType.Forbidden)]
    [InlineData(PostgresErrorCodes.SerializationFailure, "Database.ConcurrencyConflict", ErrorType.Conflict)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, "Database.ConcurrencyConflict", ErrorType.Conflict)]
    public void ConstraintAndConcurrencyErrors_GetGenericErrors(string sqlState, string code, ErrorType type)
    {
        var error = DatabaseErrorMapper.ToError(Pg(sqlState, "internal detail that must not leak"));

        error.ShouldNotBeNull();
        error.Code.ShouldBe(code);
        error.Type.ShouldBe(type);
        error.Description.ShouldNotContain("internal detail");
    }

    [Fact]
    public void A_reading_in_the_future_refused_by_its_check_is_SS104()
    {
        var check = new PostgresException("new row violates check constraint", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation,
            constraintName: DatabaseErrorMapper.FutureReadingConstraint);

        var error = DatabaseErrorMapper.ToError(check);

        error.ShouldNotBeNull();
        (error.Code, error.Type).ShouldBe(("SS104", ErrorType.BusinessRule));
        DatabaseErrorMapper.ToError(new PostgresException("other", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation, constraintName: "ck_other"))!
            .Code.ShouldBe("Database.CheckViolation");
    }

    [Fact]
    public void WrappedException_IsFound()
    {
        var wrapped = new DbUpdateException("save failed", new InvalidOperationException("outer", Pg("SS200")));

        DatabaseErrorMapper.ToError(wrapped)!.Code.ShouldBe("SS200");
    }

    [Fact]
    public void ConcurrencyException_IsAConflict() =>
        DatabaseErrorMapper.ToError(new DbUpdateConcurrencyException("changed"))!.Type.ShouldBe(ErrorType.Conflict);

    [Fact]
    public void UnexpectedErrors_AreNotMapped()
    {
        DatabaseErrorMapper.ToError(Pg("42P01")).ShouldBeNull();             // undefined_table: a bug
        DatabaseErrorMapper.ToError(new InvalidOperationException()).ShouldBeNull();
    }
}

public sealed class PlatformPersistenceTests
{
    [Theory]
    [InlineData("platform/0001_util_helpers.sql", "CREATE FUNCTION util.next_document_number")]
    [InlineData("platform/0002_audit_log.sql", "PARTITION BY RANGE (occurred_at)")]
    [InlineData("platform/0003_grants.sql", "CREATE VIEW util.v_privilege_findings")]
    [InlineData("platform/seed_ref.sql", "INSERT INTO ref.movement_type")]
    public void PlatformSqlScripts_AreEmbedded(string path, string expectedText) =>
        SqlResourceMigrationExtensions.ReadSqlResource(typeof(PlatformDatabase).Assembly, path).ShouldContain(expectedText);

    [Fact]
    public void MissingScript_FailsWithAClearMessage() =>
        Should.Throw<InvalidOperationException>(() =>
                SqlResourceMigrationExtensions.ReadSqlResource(typeof(PlatformDatabase).Assembly, "platform/missing.sql"))
            .Message.ShouldContain("SmartSpareSqlFolder");

    [Fact]
    public void PlatformModel_MatchesTheLastMigration() =>
        PlatformPersistence.Database.HasPendingModelChanges().ShouldBeFalse();

    [Fact]
    public void PlatformMigrations_KeepTheirHistoryInUtil()
    {
        using var context = PlatformDatabase.Instance.CreateContext("Host=localhost;Database=unused");

        PlatformPersistence.Database.HistorySchema.ShouldBe("util");
        context.Database.GetMigrations().First().ShouldEndWith("_InitialPlatform");
        context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditLogEntry))!
            .IsTableExcludedFromMigrations().ShouldBeTrue();
    }
}
