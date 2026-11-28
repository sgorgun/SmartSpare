using System.Numerics;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Csv;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// The use cases every catalogue shares (ADR 0025): search, read, lookup, create, update, archive and restore. Writes go
/// through the record (the aggregate) and EF in one <c>SaveChanges</c>; the version the client read is checked by
/// <c>xmin</c> (stale: <see cref="DbUpdateConcurrencyException"/> → 409), and the audit trigger records the change.
/// </summary>
public sealed class CatalogService<TContext, TRecord, TId, TDto, TWrite>(
    TContext db, IUnitOfWork<TContext> unitOfWork, ICurrentUser user, TimeProvider clock, IServiceProvider services,
    CatalogDefinition<TRecord, TId, TDto, TWrite> definition)
    where TContext : DbContext
    where TRecord : MasterRecord<TId>
    where TId : struct, INumber<TId>
    where TWrite : ICatalogWrite
{
    public async Task<Result<PagedResponse<TDto>>> ListAsync(string? search, bool archived, string? sort, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > CatalogLimits.MaxPageSize)
        {
            return CatalogErrors.InvalidPaging;
        }

        var query = Search(db.Set<TRecord>().AsNoTracking().Where(r => r.IsArchived == archived), search);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var ordered = sort switch
        {
            "name" => query.OrderBy(r => r.Name).ThenBy(r => r.Code),
            "-name" => query.OrderByDescending(r => r.Name).ThenBy(r => r.Code),
            "-code" => query.OrderByDescending(r => r.Code),
            _ => query.OrderBy(r => r.Code),
        };
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(definition.Projection)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResponse<TDto>(items, page, pageSize, total);
    }

    public async Task<Result<TDto>> GetAsync(TId id, CancellationToken cancellationToken)
    {
        var dto = await db.Set<TRecord>().AsNoTracking().Where(r => r.Id.Equals(id)).Select(definition.Projection)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return dto is null ? CatalogErrors.NotFound : dto;
    }

    public async Task<IReadOnlyList<CatalogLookupItem>> LookupAsync(string? search, int limit, CancellationToken cancellationToken) =>
        await Search(db.Set<TRecord>().AsNoTracking().Where(r => !r.IsArchived), search)
            .OrderBy(r => r.Code).Take(Math.Clamp(limit, 1, CatalogLimits.MaxLookupLimit)).Select(definition.Lookup)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Result<TDto>> CreateAsync(TWrite write, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(write, cancellationToken).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }

        return await WriteAsync(async ct =>
        {
            var created = definition.Create(write);
            if (created.IsFailure)
            {
                return created.Error;
            }

            if (await CodeTakenAsync(created.Value.Code, null, ct).ConfigureAwait(false))
            {
                return CatalogErrors.CodeTaken;
            }

            db.Add(created.Value);
            return created;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<TDto>> UpdateAsync(TId id, TWrite write, uint? version, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(write, cancellationToken).ConfigureAwait(false) is { } invalid)
        {
            return invalid;
        }

        return await ChangeAsync(id, version, async record =>
        {
            var code = definition.NormaliseCode(write.Code);
            if (code.IsFailure)
            {
                return code.Error;
            }

            if (!string.Equals(code.Value, record.Code, StringComparison.Ordinal))
            {
                if (!user.Roles.Contains(AppRoles.Administrator))
                {
                    return CatalogErrors.CodeFixed;
                }

                if (await CodeTakenAsync(code.Value, record.Id, cancellationToken).ConfigureAwait(false))
                {
                    return CatalogErrors.CodeTaken;
                }

                record.ChangeCode(code.Value);
            }

            var changed = definition.Update(record, write);
            return changed.IsFailure ? changed.Error : Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<TDto>> ArchiveAsync(TId id, uint? version, CancellationToken cancellationToken) =>
        ChangeAsync(id, version, async record =>
        {
            if (!record.IsArchived && definition.ArchiveGuard is { } guard)
            {
                var reasons = await guard(services, record, cancellationToken).ConfigureAwait(false);
                if (reasons.Count > 0)
                {
                    return CatalogErrors.ArchiveBlocked(reasons);
                }
            }

            record.Archive(clock.GetUtcNow(), user.UserId);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<TDto>> RestoreAsync(TId id, uint? version, CancellationToken cancellationToken) =>
        ChangeAsync(id, version, record =>
        {
            record.Restore();
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    /// <summary>The CSV template: the header and one example row.</summary>
    public string CsvTemplate() => CsvFile.Write(Csv.Columns, [Csv.ExampleRow]);

    /// <summary>Every active record as CSV, by code; the file imports back unchanged.</summary>
    public async Task<string> ExportCsvAsync(CancellationToken cancellationToken)
    {
        var items = await db.Set<TRecord>().AsNoTracking().Where(r => !r.IsArchived).OrderBy(r => r.Code).Select(definition.Projection)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return CsvFile.Write(Csv.Columns, items.Select(Csv.ToRow));
    }

    /// <summary>
    /// Imports a CSV file as upserts by code (STAGE_7_PLAN 2.6). Each row is read, checked by the catalogue's API
    /// validator and its code normalised; a code seen earlier in the file is a DUPLICATE error, an archived code an
    /// ARCHIVED error (import never archives or restores). Rows equal to the stored record are counted, not written. The
    /// whole import runs in one transaction and is saved, so the database checks run too; it is committed only when it
    /// is not a dry run and at most 5 % of the rows have errors, otherwise rolled back. A dry run is therefore exactly
    /// the real import, rolled back.
    /// </summary>
    public async Task<Result<CatalogImportResult>> ImportCsvAsync(string content, bool dryRun, CancellationToken cancellationToken)
    {
        var parsed = CsvFile.Parse(content, Csv.Columns, CatalogLimits.MaxCsvRows, "MasterData.Csv");
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var errors = new List<CatalogImportError>();
        var valid = new List<(int Line, string Code, TWrite Write)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (line, values) in parsed.Value.Rows)
        {
            var row = new CsvRow(line, Csv.Columns, values);
            var write = Csv.FromRow(row);
            if (row.Errors.Count == 0)
            {
                var validation = await definition.Validator.ValidateAsync(write, cancellationToken).ConfigureAwait(false);
                foreach (var failure in validation.Errors)
                {
                    row.Error(ColumnOf(failure.PropertyName), "INVALID", failure.ErrorMessage);
                }
            }

            var code = definition.NormaliseCode(write.Code);
            if (code.IsSuccess && seen.TryGetValue(code.Value, out var first))
            {
                row.Error("code", "DUPLICATE", $"The code {code.Value} is already on line {first}.");
            }
            else if (code.IsSuccess)
            {
                seen[code.Value] = line;
            }

            errors.AddRange(row.Errors);
            if (row.Errors.Count == 0)
            {
                valid.Add((line, code.Value, write));
            }
        }

        if (unitOfWork is not ITransactionalUnitOfWork transactional)
        {
            throw new InvalidOperationException("A catalogue import needs a transactional unit of work, so that a dry run can be rolled back.");
        }

        try
        {
            return await transactional.ExecuteInTransactionAsync(
                ct => UpsertAsync(valid, errors, parsed.Value.Rows.Count, dryRun, ct),
                result => result.IsSuccess && result.Value.Applied,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseErrorMapper.ToError(exception) is { } error)
        {
            return error;
        }
    }

    /// <summary>Whether the record exists (for the history endpoint).</summary>
    public Task<bool> ExistsAsync(TId id, CancellationToken cancellationToken) =>
        db.Set<TRecord>().AsNoTracking().AnyAsync(r => r.Id.Equals(id), cancellationToken);

    private CatalogCsv<TDto, TWrite> Csv =>
        definition.Csv ?? throw new InvalidOperationException($"The {definition.Noun} catalogue has no CSV definition.");

    private async Task<Result<CatalogImportResult>> UpsertAsync(
        List<(int Line, string Code, TWrite Write)> valid, List<CatalogImportError> errors, int rows, bool dryRun, CancellationToken cancellationToken)
    {
        var codes = valid.Select(v => v.Code).ToList();
        var existing = await db.Set<TRecord>().Where(r => codes.Contains(r.Code)).ToDictionaryAsync(r => r.Code, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        int created = 0, updated = 0, unchanged = 0;
        var written = new List<TRecord>();
        foreach (var (line, code, write) in valid)
        {
            if (!existing.TryGetValue(code, out var record))
            {
                var made = definition.Create(write);
                if (made.IsFailure)
                {
                    errors.Add(new CatalogImportError(line, null, "INVALID", made.Error.Description));
                    continue;
                }

                db.Add(made.Value);
                written.Add(made.Value);
                created++;
                continue;
            }

            if (record.IsArchived)
            {
                errors.Add(new CatalogImportError(line, "code", "ARCHIVED", $"{code} is archived; restore it first, an import never restores."));
                continue;
            }

            var changed = definition.Update(record, write);
            if (changed.IsFailure)
            {
                // Undo whatever the update set before it refused, so the row writes nothing.
                var entry = db.Entry(record);
                entry.CurrentValues.SetValues(entry.OriginalValues);
                errors.Add(new CatalogImportError(line, null, "INVALID", changed.Error.Description));
            }
            else if (changed.Value)
            {
                db.Entry(record).Property(r => r.UpdatedAt).IsModified = true;
                written.Add(record);
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        var withinGate = errors.Select(e => e.Line).Distinct().Count() <= rows * CatalogLimits.CsvErrorGate;
        if (withinGate)
        {
            // Saved even for a dry run, so the database's own checks answer the preview as they will the commit.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (written.Count > 0)
            {
                definition.Publish?.Invoke(unitOfWork, written);   // rolled back with the import unless it is applied
            }
        }

        return new CatalogImportResult(dryRun, withinGate && !dryRun, rows, created, updated, unchanged, errors);
    }

    /// <summary>A validator property (<c>RequiresNote</c>) as its CSV column (<c>requires_note</c>), if the catalogue has it.</summary>
    private string? ColumnOf(string property)
    {
        var name = string.Concat(property.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        return Csv.Columns.Contains(name, StringComparer.Ordinal) ? name : null;
    }

    private static IQueryable<TRecord> Search(IQueryable<TRecord> query, string? search)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return query;
        }

        var pattern = "%" + text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal) + "%";
        return query.Where(r => EF.Functions.ILike(r.Code, pattern) || EF.Functions.ILike(r.Name, pattern));
    }

    private Task<bool> CodeTakenAsync(string code, TId? except, CancellationToken cancellationToken) =>
        except is { } id
            ? db.Set<TRecord>().AsNoTracking().AnyAsync(r => r.Code == code && !r.Id.Equals(id), cancellationToken)
            : db.Set<TRecord>().AsNoTracking().AnyAsync(r => r.Code == code, cancellationToken);

    /// <summary>
    /// Runs one write in an explicit transaction of the module, as the transaction decorator does for command handlers:
    /// the transaction's start sets the session context (<c>app.user_id</c> for the audit trail), which EF would skip
    /// for a one-statement save, and its commit saves the changes. The work loads and changes the record inside it (the
    /// transaction starts from a clean change tracker). Maps the expected database errors: a stale version (409
    /// Database.ConcurrencyConflict), a code taken in the meantime, a CHECK.
    /// </summary>
    private async Task<Result<TDto>> WriteAsync(Func<CancellationToken, Task<Result<TRecord>>> work, CancellationToken cancellationToken)
    {
        Result<TRecord> outcome;
        try
        {
            if (unitOfWork is ITransactionalUnitOfWork transactional)
            {
                outcome = await transactional.ExecuteInTransactionAsync(ct => PublishingAsync(work, ct), r => r.IsSuccess, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                outcome = await work(cancellationToken).ConfigureAwait(false);
                if (outcome.IsSuccess)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (DatabaseErrorMapper.ToError(exception) is { } error)
        {
            return error;
        }

        return outcome.IsFailure ? outcome.Error : await GetAsync(outcome.Value.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the write and, if the catalogue publishes, saves it inside the transaction and queues its events.</summary>
    private async Task<Result<TRecord>> PublishingAsync(Func<CancellationToken, Task<Result<TRecord>>> work, CancellationToken cancellationToken)
    {
        var outcome = await work(cancellationToken).ConfigureAwait(false);
        if (outcome.IsSuccess && definition.Publish is { } publish)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            publish(unitOfWork, [outcome.Value]);
        }

        return outcome;
    }

    private async Task<Error?> ValidateAsync(TWrite write, CancellationToken cancellationToken)
    {
        var result = await definition.Validator.ValidateAsync(write, cancellationToken).ConfigureAwait(false);
        return result.IsValid
            ? null
            : new ValidationError(result.Errors.GroupBy(e => e.PropertyName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal));
    }

    private async Task<Result<TDto>> ChangeAsync(TId id, uint? version, Func<TRecord, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return CatalogErrors.VersionRequired;
        }

        return await WriteAsync(async ct =>
        {
            var record = await db.Set<TRecord>().SingleOrDefaultAsync(r => r.Id.Equals(id), ct).ConfigureAwait(false);
            if (record is null)
            {
                return CatalogErrors.NotFound;
            }

            db.Entry(record).Property<uint>("xmin").OriginalValue = version.Value;
            var result = await change(record).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return result.Error;
            }

            // Every write checks the version and gives the record a new one, even if no column changed.
            db.Entry(record).Property(r => r.UpdatedAt).IsModified = true;
            return record;
        }, cancellationToken).ConfigureAwait(false);
    }
}
