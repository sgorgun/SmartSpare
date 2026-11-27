using System.Numerics;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.MasterData;
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

    /// <summary>Whether the record exists (for the history endpoint).</summary>
    public Task<bool> ExistsAsync(TId id, CancellationToken cancellationToken) =>
        db.Set<TRecord>().AsNoTracking().AnyAsync(r => r.Id.Equals(id), cancellationToken);

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
                outcome = await transactional.ExecuteInTransactionAsync(work, r => r.IsSuccess, cancellationToken).ConfigureAwait(false);
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
