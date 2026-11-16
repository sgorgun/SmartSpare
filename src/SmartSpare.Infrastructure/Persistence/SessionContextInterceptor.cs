using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>Authenticated user of the current HTTP request / job (implemented in the API layer).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    /// <summary>Optional business justification for the audit trail (e.g. reason of a manual override).</summary>
    string? Reason { get; }
}

/// <summary>
/// Passes the authenticated user to PostgreSQL at the start of every transaction:
///   SELECT set_config('app.user_id', ..., true)   -- true = transaction-local (safe with connection pooling)
/// The database uses it in audit triggers (audit.audit_log.user_id) and in the role checks of
/// business functions (util.require_role). All MediatR commands run inside a transaction
/// (TransactionBehavior), so every write is attributed to a user.
/// </summary>
public sealed class SessionContextInterceptor(ICurrentUser currentUser) : DbTransactionInterceptor
{
    private const string Sql = "SELECT set_config('app.user_id', @user, true), set_config('app.reason', @reason, true)";

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateCommand(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        AddParameter(command, "user", currentUser.UserId?.ToString() ?? string.Empty);
        AddParameter(command, "reason", currentUser.Reason ?? string.Empty);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
