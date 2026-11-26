using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartSpare.Infrastructure.Persistence;

/// <summary>
/// Authenticated user of the current HTTP request or job. In a host it is <see cref="ClaimsCurrentUser"/>, which reads the
/// claims of whichever scheme authenticated the request (cookie or internal token, ADR 0016); without a request
/// (background jobs, the outbox dispatcher) there is no user.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    /// <summary>Optional business justification for the audit trail (e.g. reason of a manual override).</summary>
    string? Reason { get; }

    /// <summary>Role codes of the user (<c>role</c> claims); empty without a user.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Home warehouse of the user (<c>ss:warehouse</c> claim), if any.</summary>
    short? HomeWarehouseId { get; }
}

/// <summary>No user: background jobs, and every process that serves no requests.</summary>
internal sealed class NoCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? Reason => null;

    public IReadOnlyCollection<string> Roles => [];

    public short? HomeWarehouseId => null;
}

/// <summary>
/// The named system user that background jobs act as (ADR 0024): their writes are attributed to it in the audit trail and
/// the actor columns, with the reason <c>job:&lt;name&gt;</c>, and it has no roles. A well-known id, not a database row:
/// actor columns carry no foreign keys (MODULE_MAPPING 2.1), and screens show it as "System".
/// </summary>
public static class SystemUser
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-4000-8000-00000000f001");

    public const string Name = "System";
}

/// <summary>
/// Overrides <see cref="ICurrentUser"/> for one scope of work done on behalf of a user who is not (yet) the
/// request's principal. Sign-in uses it: failed-attempt counters, lockouts and rehashes are written while the user
/// is signing in, so the audit must name that user, with the reason <c>sign-in</c> (STAGE_3_PLAN section 2.2).
/// Registered per DI scope; <see cref="ActAs"/> returns a handle that ends the override.
/// </summary>
public sealed class ActingUser
{
    private (Guid UserId, string Reason)? _current;

    internal Guid? UserId => _current?.UserId;

    internal string? Reason => _current?.Reason;

    internal bool IsSet => _current is not null;

    public IDisposable ActAs(Guid userId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_current is not null)
        {
            throw new InvalidOperationException("An acting user is already set in this scope.");
        }

        _current = (userId, reason);
        return new Scope(this);
    }

    private sealed class Scope(ActingUser owner) : IDisposable
    {
        public void Dispose() => owner._current = null;
    }
}

/// <summary>
/// Passes the session context to PostgreSQL at the start of every transaction of a module DbContext:
///   SELECT set_config('app.user_id', @user, true), set_config('app.reason', @reason, true),
///          set_config('app.user_roles', @roles, true), set_config('app.home_warehouse_id', @warehouse, true)
/// <c>app.user_roles</c> is the comma-separated list of role codes, <c>app.home_warehouse_id</c> the warehouse id.
/// The third argument <c>true</c> makes the values transaction-local: they vanish at commit or
/// rollback, so a pooled connection never carries them to the next request. An empty value means
/// "none" (util.current_app_user() returns NULL, e.g. for background jobs).
/// The database reads them in the audit triggers (audit.audit_log.user_id, reason), in the role checks of
/// business functions (util.require_role reads app.user_roles) and in the warehouse checks of dispatch and receive
/// (app.home_warehouse_id). Commands run inside a transaction opened by the transaction decorator, so every write is
/// attributed. An <see cref="ActingUser"/> set in the scope takes precedence over <see cref="ICurrentUser"/>.
/// Trust model: MODULE_MAPPING section 3.6.
/// </summary>
public sealed class SessionContextInterceptor(ICurrentUser currentUser, ActingUser actingUser) : DbTransactionInterceptor
{
    private const string Sql =
        "SELECT set_config('app.user_id', @user, true), set_config('app.reason', @reason, true), "
        + "set_config('app.user_roles', @roles, true), set_config('app.home_warehouse_id', @warehouse, true)";

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

    /// <summary>Role codes joined with ','; a code that could break the list is left out rather than trusted.</summary>
    internal static string RolesValue(IEnumerable<string> roles) =>
        string.Join(',', roles
            .Where(r => !string.IsNullOrWhiteSpace(r) && !r.Contains(',', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    private DbCommand CreateCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;

        // A user acting during sign-in has no roles or warehouse in that transaction: sign-in calls no business function.
        var acting = actingUser.IsSet;
        AddParameter(command, "user", (acting ? actingUser.UserId : currentUser.UserId)?.ToString() ?? string.Empty);
        AddParameter(command, "reason", (acting ? actingUser.Reason : currentUser.Reason) ?? string.Empty);
        AddParameter(command, "roles", acting ? string.Empty : RolesValue(currentUser.Roles));
        AddParameter(command, "warehouse",
            (acting ? null : currentUser.HomeWarehouseId)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
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
