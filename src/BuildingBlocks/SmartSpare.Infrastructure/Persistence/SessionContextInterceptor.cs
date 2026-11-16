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

/// <summary>Default until the BFF provides the signed-in user (stage 3): no user, like a background job.</summary>
internal sealed class NoCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? Reason => null;
}

/// <summary>
/// Passes the acting user to PostgreSQL at the start of every transaction of a module DbContext:
///   SELECT set_config('app.user_id', @user, true), set_config('app.reason', @reason, true)
/// The third argument <c>true</c> makes the values transaction-local: they vanish at commit or
/// rollback, so a pooled connection never carries them to the next request. An empty value means
/// "no user" (util.current_app_user() returns NULL, e.g. for background jobs).
/// The database reads them in the audit triggers (audit.audit_log.user_id, reason) and in the role
/// checks of business functions (util.require_role). Commands run inside a transaction opened by
/// the transaction decorator, so every write is attributed. Trust model: MODULE_MAPPING section 3.6.
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
