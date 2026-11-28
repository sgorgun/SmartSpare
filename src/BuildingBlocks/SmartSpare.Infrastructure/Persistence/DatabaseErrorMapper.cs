using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Turns expected database errors into <see cref="Error"/> values, so handlers return a
/// <see cref="Result"/> instead of letting the exception escape (ADR 0002, Result pattern).
/// Business rules raised by triggers and functions use custom SQLSTATEs SS100–SS402 and the catalogue codes SS500–SS501
/// (docs/database/DATABASE_DESIGN.md §6.3); constraint violations are the last line of defence.
/// Anything else is unexpected and stays an exception.
/// </summary>
public static class DatabaseErrorMapper
{
    /// <summary>Maps <paramref name="exception"/> (or the exception it wraps) to an error; <see langword="null"/> if unexpected.</summary>
    public static Error? ToError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is DbUpdateConcurrencyException)
        {
            return Error.Conflict("Database.ConcurrencyConflict", "The record was changed by someone else. Reload it and try again.");
        }

        return FindPostgresException(exception) is { } pg ? ToError(pg) : null;
    }

    /// <summary>Maps a PostgreSQL error by its SQLSTATE; <see langword="null"/> if it is not an expected one.</summary>
    public static Error? ToError(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var state = exception.SqlState;

        // Business rules: the message is written for the user by the raising function.
        ErrorType? businessType = state switch
        {
            "SS100" or "SS101" or "SS102" or "SS103" or "SS104" => ErrorType.BusinessRule,   // engine hours (UC02)
            "SS200" or "SS201" => ErrorType.Conflict,                             // stock (NFR03, US10)
            "SS300" => ErrorType.Conflict,                                        // state machines
            "SS301" or "SS302" => ErrorType.Forbidden,                            // role / warehouse in the database
            "SS400" or "SS401" or "SS402" => ErrorType.BusinessRule,              // supplier conditions (US12)
            "SS500" => ErrorType.BusinessRule,                                    // an archived record chosen (ADR 0025)
            "SS501" => ErrorType.Conflict,                                        // archiving refused while in use (ADR 0025)
            _ => null,
        };
        if (businessType is { } type)
        {
            return new Error(state, exception.MessageText, type);
        }

        // A reading time in the future is a business rule (SS104) that only a CHECK guards in the database.
        if (state == PostgresErrorCodes.CheckViolation && exception.ConstraintName == FutureReadingConstraint)
        {
            return Error.BusinessRule("SS104", "The reading time cannot be in the future.");
        }

        // Constraint and concurrency errors: generic description; the constraint name helps support.
        var constraint = exception.ConstraintName is { Length: > 0 } name ? $" ({name})" : string.Empty;
        return state switch
        {
            PostgresErrorCodes.UniqueViolation =>
                Error.Conflict("Database.UniqueViolation", "A record with the same value already exists" + constraint + "."),
            PostgresErrorCodes.ForeignKeyViolation =>
                Error.BusinessRule("Database.ForeignKeyViolation", "A referenced record does not exist or is still in use" + constraint + "."),
            PostgresErrorCodes.CheckViolation =>
                Error.BusinessRule("Database.CheckViolation", "The data violates a validation rule" + constraint + "."),
            PostgresErrorCodes.ExclusionViolation =>
                Error.BusinessRule("Database.ExclusionViolation", "The data overlaps an existing record" + constraint + "."),
            PostgresErrorCodes.NotNullViolation =>
                Error.BusinessRule("Database.NotNullViolation", "A required value is missing" + (exception.ColumnName is { Length: > 0 } column ? $" ({column})" : string.Empty) + "."),
            PostgresErrorCodes.InsufficientPrivilege =>
                Error.Forbidden("Database.InsufficientPrivilege", "The operation is not permitted."),
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable =>
                Error.Conflict("Database.ConcurrencyConflict", "The data was changed concurrently. Please try again."),
            _ => null,
        };
    }

    /// <summary>The CHECK of <c>fleet.engine_hours_reading</c> that refuses readings in the future (SS104).</summary>
    public const string FutureReadingConstraint = "ck_engine_hours_reading_not_future";

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }
}
