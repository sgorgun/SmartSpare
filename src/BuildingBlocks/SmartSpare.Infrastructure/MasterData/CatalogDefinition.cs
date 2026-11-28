using System.Linq.Expressions;
using System.Numerics;
using FluentValidation;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// What a catalogue supplies to the shared mechanism (ADR 0025, STAGE_7_PLAN 2.2): its route, policies, projection,
/// how a write creates or changes a record, its archive guard and its OpenAPI examples. <c>MapCatalog</c> builds the
/// endpoints from it; every catalogue therefore behaves the same (search, paging, ETag/If-Match, archive with guard,
/// history).
/// </summary>
public sealed record CatalogDefinition<TRecord, TId, TDto, TWrite>
    where TRecord : MasterRecord<TId>
    where TId : struct, INumber<TId>
    where TWrite : ICatalogWrite
{
    /// <summary>Path below the module's <c>/api/v1/{module}</c> group, e.g. <c>/adjustment-reasons</c>.</summary>
    public required string Route { get; init; }

    /// <summary>The module's API base for <c>Location</c> headers, e.g. <c>/api/v1/inventory</c>.</summary>
    public required string ApiBase { get; init; }

    /// <summary>OpenAPI tag.</summary>
    public required string Tag { get; init; }

    /// <summary>Singular in plain words, e.g. "adjustment reason".</summary>
    public required string Noun { get; init; }

    /// <summary>Singular in PascalCase for operation ids, e.g. <c>AdjustmentReason</c>.</summary>
    public required string OperationNoun { get; init; }

    /// <summary>Permission to list, read, look up and see the history (ADR 0028).</summary>
    public required string ViewPermission { get; init; }

    public required string CreatePermission { get; init; }

    public required string EditPermission { get; init; }

    /// <summary>Permission to archive and restore.</summary>
    public required string ArchivePermission { get; init; }

    /// <summary>Schema-qualified table, as the audit trail names it and <c>audit.history_table</c> allows it.</summary>
    public required string AuditTable { get; init; }

    public required Expression<Func<TRecord, TDto>> Projection { get; init; }

    /// <summary>The version (<c>xmin</c>) of a projected record, sent as the <c>ETag</c>.</summary>
    public required Func<TDto, string> VersionOf { get; init; }

    /// <summary>The id of a projected record, for the <c>Location</c> of a created one.</summary>
    public required Func<TDto, string> IdOf { get; init; }

    public required Expression<Func<TRecord, CatalogLookupItem>> Lookup { get; init; }

    /// <summary>Normalises a code as <see cref="Create"/> stores it (for uniqueness checks and code changes).</summary>
    public required Func<string?, Result<string>> NormaliseCode { get; init; }

    public required Func<TWrite, Result<TRecord>> Create { get; init; }

    /// <summary>Applies everything but the code; <see langword="true"/> if anything changed.</summary>
    public required Func<TRecord, TWrite, Result<bool>> Update { get; init; }

    /// <summary>What still uses the record (empty: it may be archived). None: nothing blocks archiving.</summary>
    public Func<IServiceProvider, TRecord, CancellationToken, Task<IReadOnlyList<string>>>? ArchiveGuard { get; init; }

    public required IValidator<TWrite> Validator { get; init; }

    /// <summary>
    /// Integration events of a write (for example <c>BrandCatalogChanged</c>): called with the created or changed records
    /// after they were saved in the write's transaction, so they have their ids; events it adds to the unit of work commit
    /// with the change (outbox). Also called once for the records an applied CSV import wrote. None: nothing is published.
    /// </summary>
    public Action<SmartSpare.Infrastructure.Persistence.IUnitOfWork, IReadOnlyList<TRecord>>? Publish { get; init; }

    /// <summary>CSV template, export and import (STAGE_7_PLAN 2.6); none: the catalogue has no CSV endpoints.</summary>
    public CatalogCsv<TDto, TWrite>? Csv { get; init; }

    /// <summary>Column name → field name in plain words for the history; unlisted columns keep their name.</summary>
    public IReadOnlyDictionary<string, string> FieldLabels { get; init; } = new Dictionary<string, string>();

    /// <summary>A sentence on who may change the catalogue and what archiving checks, appended to every description.</summary>
    public required string Rules { get; init; }

    public required TDto Example { get; init; }

    public required TWrite WriteExample { get; init; }

    public required CatalogLookupItem LookupExample { get; init; }
}
