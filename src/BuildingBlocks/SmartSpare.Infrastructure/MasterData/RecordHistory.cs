using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// Names of the people behind audit entries (account ids of <c>audit.audit_log.user_id</c>). A module with catalogues
/// registers an implementation over Identity's employee directory; ids it cannot name (the system user) are left out.
/// </summary>
public interface IActorNames
{
    Task<IReadOnlyDictionary<Guid, string>> FullNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

/// <summary>
/// One page of a catalogue record's change history in plain words (ADR 0025, STAGE_7_PLAN 2.5), read through
/// <c>audit.record_history</c> in the module's connection: who (first name and initial), what (each changed field,
/// old → new) and when.
/// </summary>
public static class RecordHistory
{
    private static readonly HashSet<string> TechnicalColumns = new(StringComparer.Ordinal) { "id", "created_at", "updated_at", "archived_at", "archived_by" };

    public static async Task<PagedResponse<RecordHistoryEntry>> ReadAsync(
        DbContext db, IActorNames names, string table, string recordId, int page, int pageSize,
        IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(labels);
        var rows = await db.Database.SqlQueryRaw<HistoryRow>(
                // Column names in snake_case: the module contexts map every type, this one too, by that convention.
                "SELECT occurred_at, action, user_id, changed_columns, old_values::text AS old_values, new_values::text AS new_values, "
                + "reason, total_count FROM audit.record_history({0}, {1}, {2}, {3})",
                table, recordId, page, pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var userIds = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToArray();
        var actors = userIds.Length == 0 ? new Dictionary<Guid, string>() : await names.FullNamesAsync(userIds, cancellationToken).ConfigureAwait(false);
        var entries = rows.Select(r => new RecordHistoryEntry(
                r.OccurredAt,
                ActionOf(r),
                r.UserId is { } id && actors.TryGetValue(id, out var name) ? ShortName(name) : null,
                ChangesOf(r, labels),
                r.Reason))
            .ToList();
        return new PagedResponse<RecordHistoryEntry>(entries, page, pageSize, rows.Count == 0 ? 0 : (int)rows[0].TotalCount);
    }

    /// <summary>"Rasa Kazlauskienė" → "Rasa K." (the form the history panel shows).</summary>
    public static string ShortName(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => fullName,
            1 => parts[0],
            _ => $"{parts[0]} {char.ToUpper(parts[^1][0], CultureInfo.InvariantCulture)}.",
        };
    }

    private static string ActionOf(HistoryRow row)
    {
        if (row.Action == "INSERT")
        {
            return "created";
        }

        if (row.Action == "DELETE")
        {
            return "deleted";
        }

        if (row.ChangedColumns?.Contains("is_archived") == true && Values(row.NewValues).TryGetValue("is_archived", out var archived))
        {
            return archived == "true" ? "archived" : "restored";
        }

        return "changed";
    }

    private static List<RecordChange> ChangesOf(HistoryRow row, IReadOnlyDictionary<string, string> labels)
    {
        var before = Values(row.OldValues);
        var after = Values(row.NewValues);
        var columns = row.Action == "UPDATE" ? row.ChangedColumns ?? [] : [.. after.Keys.Union(before.Keys, StringComparer.Ordinal)];
        return
        [
            .. columns.Where(c => !TechnicalColumns.Contains(c) && c != "is_archived")
                .Order(StringComparer.Ordinal)
                .Select(c => new RecordChange(
                    labels.TryGetValue(c, out var label) ? label : c.Replace('_', ' '),
                    before.GetValueOrDefault(c),
                    after.GetValueOrDefault(c))),
        ];
    }

    private static Dictionary<string, string?> Values(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetRawText(),
            },
            StringComparer.Ordinal);
    }

    /// <summary>A row of <c>audit.record_history</c>.</summary>
    internal sealed record HistoryRow(
        DateTimeOffset OccurredAt, string Action, Guid? UserId, string[]? ChangedColumns, string? OldValues, string? NewValues, string? Reason, long TotalCount);
}
