using Npgsql;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Maps PostgreSQL errors raised by constraints / business functions to HTTP problem details.
/// Custom SQLSTATEs are defined in db/migrations/V011 (header) and docs/database/DATABASE_DESIGN.md.
/// </summary>
public static class DatabaseErrorMapper
{
    public static (int Status, string Title)? Map(Exception exception) =>
        exception.GetBaseException() is not PostgresException pg ? null : pg.SqlState switch
        {
            "SS100" => (422, "Engine hours cannot decrease"),
            "SS101" => (422, "Implausible engine-hour reading"),
            "SS102" or "SS103" => (422, "Invalid engine-hour reading"),
            "SS200" => (409, "Insufficient stock"),
            "SS201" => (409, "Donor warehouse would fall below safety stock"),
            "SS300" => (409, "Invalid status transition"),
            "SS301" or "SS302" or "42501" => (403, "Operation not permitted"),
            "SS400" or "SS401" or "SS402" => (422, "Supplier conditions violated"),
            PostgresErrorCodes.UniqueViolation => (409, "Duplicate value"),
            PostgresErrorCodes.ForeignKeyViolation => (422, "Referenced record does not exist"),
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ExclusionViolation => (422, "Data validation failed"),
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => (409, "Concurrent update, please retry"),
            _ => null
        };
}
