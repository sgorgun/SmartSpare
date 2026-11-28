using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Csv;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Features.PartCatalog;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Imports;

internal static class ImportErrors
{
    public static readonly Error Duplicate = Error.Conflict(
        "Inventory.ImportDuplicate", "This file was imported before (same content). Change the file or open the earlier batch.");

    public static readonly Error NotFound = Error.NotFound("Inventory.ImportNotFound", "The import batch does not exist.");

    public static readonly Error NotUtf8 = Error.Validation("Inventory.ImportEncoding", "The file must be UTF-8 text.");
}

/// <summary>Imports a CSV file: raw rows, then <c>staging.process_batch</c> validates and loads with its quality gate.</summary>
internal sealed record ImportFileCommand(string Source, string FileName, byte[] Content, bool DryRun = false) : ICommand<ImportBatchDetails>;

internal sealed class ImportFileCommandValidator : AbstractValidator<ImportFileCommand>
{
    public ImportFileCommandValidator()
    {
        RuleFor(c => c.Source).Must(s => ImportSources.All.Contains(s))
            .WithMessage($"'Source' must be one of: {string.Join(", ", ImportSources.All)}.");
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(255);
        RuleFor(c => c.Content).NotEmpty().WithMessage("The file is empty.")
            .Must(c => c.LongLength <= ImportLimits.MaxFileBytes).WithMessage("A file has at most 10 MB.");
    }
}

/// <summary>
/// One transaction (STAGE_5_PLAN section 5): refuse a file imported before, store the batch and its rows in <c>raw</c>
/// with a binary COPY, call <c>staging.process_batch</c>. A file over the 5 % error threshold is a normal answer with
/// status failed, not an error. Nothing is published yet: <c>PartCatalogChanged</c> follows with its first consumer (#12).
/// </summary>
internal sealed class ImportFileCommandHandler(
    InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, ImportStore store)
    : ICommandHandler<ImportFileCommand, ImportBatchDetails>
{
    public async Task<Result<ImportBatchDetails>> Handle(ImportFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(command.Content);
        }
        catch (DecoderFallbackException)
        {
            return ImportErrors.NotUtf8;
        }

        var parsed = CsvImportFile.Parse(text, command.Source);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var source = ImportMapping.ToDomain(command.Source);
        var sha = Convert.ToHexStringLower(SHA256.HashData(command.Content));
        if (await db.Set<ImportBatch>().AsNoTracking().AnyAsync(b => b.Source == source && b.FileSha256 == sha, cancellationToken).ConfigureAwait(false))
        {
            return ImportErrors.Duplicate;
        }

        if (command.DryRun)
        {
            await store.BeginDryRunAsync(cancellationToken).ConfigureAwait(false);
        }

        var batch = new ImportBatch
        {
            Source = source,
            FileName = Path.GetFileName(command.FileName),
            FileSha256 = sha,
            Status = ImportStatus.Uploaded,
            TotalRows = parsed.Value.Rows.Count,
            UploadedBy = user.UserId,
        };
        db.Add(batch);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await store.CopyRowsAsync(batch.Id, command.Source, parsed.Value, cancellationToken).ConfigureAwait(false);
        await store.ProcessBatchAsync(batch.Id, cancellationToken).ConfigureAwait(false);
        if (source == ImportSource.PartCatalog && !command.DryRun)
        {
            // #12: the parts this load changed go to the outbox in the same transaction (STAGE_6_PLAN 5.2).
            var changed = await PartCatalogSnapshots.ChangedByBatchAsync(db, batch.Id, cancellationToken).ConfigureAwait(false);
            PartCatalogPublication.Queue(unitOfWork, changed);
        }

        db.ChangeTracker.Clear();
        var details = (await ImportReader.ReadAsync(db, batch.Id, cancellationToken).ConfigureAwait(false))!;
        if (command.DryRun)
        {
            // STAGE_7_PLAN 2.6: the dry run is the real import, rolled back after its report was read.
            await store.EndDryRunAsync(cancellationToken).ConfigureAwait(false);
        }

        return details;
    }
}

/// <summary>API names of the <c>raw</c> enums.</summary>
internal static class ImportMapping
{
    public static ImportSource ToDomain(string source) =>
        source == ImportSources.PartCatalog ? ImportSource.PartCatalog : ImportSource.DemandHistory;

    public static string ToApi(ImportSource source) =>
        source == ImportSource.PartCatalog ? ImportSources.PartCatalog : ImportSources.DemandHistory;

    public static string ToApi(ImportStatus status) => status switch
    {
        ImportStatus.Uploaded => ImportStatuses.Uploaded,
        ImportStatus.Validated => ImportStatuses.Validated,
        ImportStatus.Loaded => ImportStatuses.Loaded,
        _ => ImportStatuses.Failed,
    };

    public static ImportBatchResponse ToResponse(ImportBatch b) => new(
        b.Id, ToApi(b.Source), b.FileName, ToApi(b.Status), b.TotalRows, b.ValidRows, b.ErrorRows, b.LoadedRows, b.UploadedBy,
        b.UploadedAt, b.ProcessedAt, b.Message);
}

internal sealed record ListImportsQuery(int Page, int PageSize) : IQuery<PagedResponse<ImportBatchResponse>>;

internal sealed class ListImportsQueryValidator : AbstractValidator<ListImportsQuery>
{
    public ListImportsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, ImportLimits.MaxPageSize);
    }
}

internal sealed record GetImportQuery(long Id) : IQuery<ImportBatchDetails>;

internal static class ImportEndpoints
{
    /// <summary>Rate limit of uploads: per user where the request is already authenticated (services), else per client address.</summary>
    public const string RateLimitPolicy = "inventory-import";

    public static IServiceCollection AddImportRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirst(AppClaimTypes.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = ImportLimits.PermitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

    private static readonly Error SourceError =
        Error.Validation("Inventory.ImportSource", $"The source must be one of: {string.Join(", ", ImportSources.All)}.");

    private static IResult Csv(string text, string name) => Results.File(Encoding.UTF8.GetBytes(text), "text/csv; charset=utf-8", name);

    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapPost("/imports", async (
                [FromForm][Description("partCatalog or demandHistory.")] string source,
                [Description("The CSV file, UTF-8, at most 10 MB and 50,000 rows.")] IFormFile file,
                ICommandHandler<ImportFileCommand, ImportBatchDetails> handler,
                CancellationToken cancellationToken,
                [Description("true runs the whole import and rolls it back: 200 with the report the import would give.")] bool dryRun = false) =>
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var result = await handler.Handle(new ImportFileCommand(source, file.FileName, buffer.ToArray(), dryRun), cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.Error.ToProblem();
                }

                return dryRun ? Results.Ok(result.Value) : Results.Created($"/api/v1/inventory/imports/{result.Value.Batch.Id}", result.Value);
            })
            .RequireAuthorization(Permissions.Inventory.Import.Run)
            .RequireRateLimiting(RateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(ImportLimits.MaxFileBytes + (64 * 1024)))
            .DisableAntiforgery()
            .WithName("ImportFile")
            .WithSummary("Imports a part catalogue or demand history CSV.")
            .WithDescription(
                "Multipart form with source (partCatalog or demandHistory) and file. The file is UTF-8 CSV with a header row; "
                + "comma or semicolon separated; columns for partCatalog: brand_code, oem_part_number, description, "
                + "category_code, uom_code, weight_kg, unit_cost, condition_based; for demandHistory: doc_date (YYYY-MM-DD), "
                + "warehouse_code, brand_code, oem_part_number, quantity, doc_ref. The rows are stored as raw text, validated "
                + "into staging and loaded into the catalogue or the demand ledger in one transaction (raw → staging → core). "
                + "If more than 5 % of the rows are invalid nothing is loaded and the batch is answered with status failed "
                + "(still 201): the report lists the row errors. With dryRun=true the same import runs and is rolled back: 200 "
                + "with the report it would give; its batch is not kept. The same file cannot be imported twice (409 "
                + "Inventory.ImportDuplicate). At most 10 MB, 50,000 rows and 5 imports a minute. Permission Inventory.Import.Run "
                + "(administrators and inventory planners).")
            .Produces<ImportBatchDetails>(StatusCodes.Status201Created)
            .Produces<ImportBatchDetails>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseExample(StatusCodes.Status201Created, InventoryExamples.ImportDetails)
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.Problem(Error.Validation(
                "Inventory.ImportHeader", "The header must name exactly these columns: doc_date, warehouse_code, brand_code, oem_part_number, quantity, doc_ref. Missing: doc_ref.")))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status409Conflict, InventoryExamples.Problem(ImportErrors.Duplicate));

        group.MapGet("/imports/template", (
                [Description("partCatalog or demandHistory.")] string source) =>
                ImportSources.Columns.TryGetValue(source, out var columns)
                    ? Csv(CsvFile.Write(columns, [ImportTemplates.ExampleRow(source)]), $"{source}-template.csv")
                    : SourceError.ToProblem())
            .RequireAuthorization(Permissions.Inventory.Import.Run)
            .WithName("GetImportTemplate")
            .WithSummary("Downloads the CSV template of a part catalogue or demand history import.")
            .WithDescription("UTF-8 CSV with the source's header and one example row. Permission Inventory.Import.Run.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/imports/export", async (
                [Description("partCatalog or demandHistory.")] string source,
                ImportStore store,
                CancellationToken cancellationToken) =>
                ImportSources.Columns.TryGetValue(source, out var columns)
                    ? Csv(CsvFile.Write(columns, await store.ExportRowsAsync(source, cancellationToken).ConfigureAwait(false)), $"{source}.csv")
                    : SourceError.ToProblem())
            .RequireAuthorization(Permissions.Inventory.Import.Run)
            .WithName("ExportImportSource")
            .WithSummary("Exports the active parts or the demand history in the import's columns.")
            .WithDescription(
                "partCatalog: every active part by brand and OEM number; demandHistory: every historical demand movement of the "
                + "ledger. The columns are the import's, so the file can be edited and imported (a demand history file "
                + "imported again is refused as a duplicate). Permission Inventory.Import.Run.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/imports", async (
                IQueryHandler<ListImportsQuery, PagedResponse<ImportBatchResponse>> handler,
                CancellationToken cancellationToken,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Batches per page, 1 to 100.")] int pageSize = ImportLimits.DefaultPageSize) =>
                (await handler.Handle(new ListImportsQuery(page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Import.View)
            .WithName("ListImports")
            .WithSummary("Lists import batches, newest first.")
            .WithDescription("Every import batch with its load report. Permission Inventory.Import.View.")
            .Produces<PagedResponse<ImportBatchResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<ImportBatchResponse>(
                [InventoryExamples.ImportDetails.Batch], Page: 1, PageSize: ImportLimits.DefaultPageSize, TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                "PageSize", "'Page Size' must be between 1 and 100. You entered 500."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);

        group.MapGet("/imports/{id:long}", async (
                [Description("Import batch id.")] long id,
                IQueryHandler<GetImportQuery, ImportBatchDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(new GetImportQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Inventory.Import.View)
            .WithName("GetImport")
            .WithSummary("Returns an import batch with its row errors.")
            .WithDescription(
                "The batch, its load report and its first 200 row errors in file order, with the total count. An unknown "
                + "id gives 404 with code Inventory.ImportNotFound. Permission Inventory.Import.View.")
            .Produces<ImportBatchDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.ImportDetails)
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(ImportErrors.NotFound));
    }
}

/// <summary>The example rows of the import templates, valid against the demo data.</summary>
internal static class ImportTemplates
{
    public static IReadOnlyList<string?> ExampleRow(string source) => source == ImportSources.PartCatalog
        ? ["JD", "RE504836", "Oil filter element", "OIL_FILTER", "PCS", "0.450", "18.90", "false"]
        : ["2026-06-30", "KLP", "JD", "RE504836", "2", "INV-2026-0042"];
}
