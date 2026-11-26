using System.ComponentModel;

namespace SmartSpare.ApiContracts.Inventory;

/// <summary>What a CSV import loads (the <c>raw.import_source</c> enum).</summary>
public static class ImportSources
{
    public const string PartCatalog = "partCatalog";

    public const string DemandHistory = "demandHistory";

    public static IReadOnlyList<string> All { get; } = [PartCatalog, DemandHistory];

    /// <summary>The header row each source expects, in this order (extra columns are refused, order is free).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Columns { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [PartCatalog] = ["brand_code", "oem_part_number", "description", "category_code", "uom_code", "weight_kg", "unit_cost", "condition_based"],
        [DemandHistory] = ["doc_date", "warehouse_code", "brand_code", "oem_part_number", "quantity", "doc_ref"],
    };
}

/// <summary>Values of <see cref="ImportBatchResponse.Status"/>.</summary>
public static class ImportStatuses
{
    public const string Uploaded = "uploaded";
    public const string Validated = "validated";
    public const string Loaded = "loaded";
    public const string Failed = "failed";
}

/// <summary>Limits of the CSV import (STAGE_5_PLAN section 5).</summary>
public static class ImportLimits
{
    /// <summary>Largest accepted file: 10 MB.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public const int MaxRows = 50_000;

    /// <summary>Row errors returned with a batch; the rest are counted.</summary>
    public const int MaxErrorsShown = 200;

    /// <summary>Imports per user (or client address) and minute.</summary>
    public const int PermitsPerMinute = 5;

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

/// <summary>An import batch and its load report.</summary>
[Description("An import batch and its load report.")]
public sealed record ImportBatchResponse(
    [property: Description("Batch id.")] long Id,
    [property: Description("partCatalog or demandHistory.")] string Source,
    [property: Description("Name of the uploaded file.")] string FileName,
    [property: Description("uploaded, validated, loaded or failed (the quality gate refused the file).")] string Status,
    [property: Description("Data rows in the file.")] int TotalRows,
    [property: Description("Rows without errors.")] int? ValidRows,
    [property: Description("Rows with at least one error.")] int? ErrorRows,
    [property: Description("Rows loaded or updated in the catalogue or the ledger.")] int? LoadedRows,
    [property: Description("Id of the user who uploaded the file.")] Guid? UploadedBy,
    [property: Description("When the file was uploaded.")] DateTimeOffset UploadedAt,
    [property: Description("When the batch was processed.")] DateTimeOffset? ProcessedAt,
    [property: Description("The load report or the reason the batch failed.")] string? Message);

/// <summary>One validation error of an imported row.</summary>
[Description("One validation error of an imported row.")]
public sealed record ImportRowError(
    [property: Description("Line in the file; the header is line 1, the first data row line 2.")] int LineNo,
    [property: Description("Column of the error, for example unit_cost.")] string Column,
    [property: Description("REQUIRED, FORMAT, TYPE, RANGE, UNKNOWN_REF or DUPLICATE.")] string Code,
    [property: Description("What is wrong with the value.")] string Message);

/// <summary>An import batch with its row errors.</summary>
[Description("An import batch with its row errors.")]
public sealed record ImportBatchDetails(
    [property: Description("The batch and its load report.")] ImportBatchResponse Batch,
    [property: Description("Row errors in file order, at most 200.")] IReadOnlyList<ImportRowError> Errors,
    [property: Description("All row errors of the batch.")] int ErrorCount);
