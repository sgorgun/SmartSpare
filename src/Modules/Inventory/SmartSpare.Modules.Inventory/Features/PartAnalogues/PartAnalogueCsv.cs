using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Csv;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.PartAnalogues;

/// <summary>
/// The CSV of part analogue links (ADR 0027 decision 7, PR 7i2b), in the shape of the catalogue CSV endpoints so the
/// shared CSV panel works unchanged. A row names a pair by brand and OEM number. It creates the link, or updates the
/// factor, date and note of the same link; an import never removes or retypes a link, because those need the user's
/// reason (decision 6) and are done on the part's Analogues tab. Every row is checked against the existing links and the
/// rows before it with the rules the API uses, and the errors name the line, the column and the rule. The 5 % gate and
/// the rolled-back dry run are those of every catalogue import.
/// </summary>
internal sealed class PartAnalogueCsv(InventoryDbContext db, IUnitOfWork<InventoryDbContext> unitOfWork, ICurrentUser user, TimeProvider clock)
{
    public const string BrandCode = "brand_code";
    public const string OemPartNumber = "oem_part_number";
    public const string AnalogueBrandCode = "analogue_brand_code";
    public const string AnalogueOemPartNumber = "analogue_oem_part_number";
    public const string LinkTypeColumn = "link_type";
    public const string ScalingFactor = "scaling_factor";
    public const string ValidFrom = "valid_from";
    public const string Note = "note";

    public static readonly IReadOnlyList<string> Columns = [BrandCode, OemPartNumber, AnalogueBrandCode, AnalogueOemPartNumber, LinkTypeColumn, ScalingFactor];

    public static readonly IReadOnlyList<string> Optional = [ValidFrom, Note];

    public static readonly IReadOnlyList<string> AllColumns = [.. Columns, .. Optional];

    public static string Template() => CsvFile.Write(AllColumns, [["JD", "RE504836", "MANN", "W1170-12", AnalogueLinkTypes.Interchangeable, "1", "2026-11-28", "OEM and aftermarket"]]);

    public async Task<string> ExportAsync(CancellationToken cancellationToken)
    {
        var parts = await PartKeysAsync(cancellationToken).ConfigureAwait(false);
        var links = await db.Set<PartAnalogue>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = links
            .Select(l => (Part: parts.ById[l.PartId], Analogue: parts.ById[l.AnaloguePartId], Link: l))
            .OrderBy(r => r.Part.Brand, StringComparer.Ordinal).ThenBy(r => r.Part.Oem, StringComparer.Ordinal).ThenBy(r => r.Analogue.Oem, StringComparer.Ordinal)
            .Select(r => (IReadOnlyList<string?>)
            [
                r.Part.Brand, r.Part.Oem, r.Analogue.Brand, r.Analogue.Oem, TypeName(r.Link.LinkType),
                r.Link.ScalingFactor.ToString("0.###", CultureInfo.InvariantCulture), r.Link.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Link.Note,
            ]);
        return CsvFile.Write(AllColumns, rows);
    }

    public async Task<Result<CatalogImportResult>> ImportAsync(string content, bool dryRun, CancellationToken cancellationToken)
    {
        var parsed = CsvFile.Parse(content, Columns, CatalogLimits.MaxCsvRows, "MasterData.Csv", Optional);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        if (unitOfWork is not ITransactionalUnitOfWork transactional)
        {
            throw new InvalidOperationException("An analogue import needs a transactional unit of work, so that a dry run can be rolled back.");
        }

        try
        {
            return await transactional.ExecuteInTransactionAsync(
                ct => UpsertAsync(parsed.Value, dryRun, ct), result => result.IsSuccess && result.Value.Applied, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseErrorMapper.ToError(exception) is { } error)
        {
            return error;
        }
    }

    private async Task<Result<CatalogImportResult>> UpsertAsync(CsvFile.Parsed file, bool dryRun, CancellationToken cancellationToken)
    {
        var parts = await PartKeysAsync(cancellationToken).ConfigureAwait(false);
        var links = await db.Set<PartAnalogue>().ToListAsync(cancellationToken).ConfigureAwait(false);
        var facts = links.Select(l => new AnalogueLinkFact(l.Id, l.PartId, l.AnaloguePartId, l.LinkType)).ToList();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var seenPairs = new Dictionary<(long, long), int>();
        var errors = new List<CatalogImportError>();
        int created = 0, updated = 0, unchanged = 0;

        foreach (var (line, values) in file.Rows)
        {
            var row = new CsvRow(line, file.Columns, values);
            var part = Resolve(row, parts, BrandCode, OemPartNumber);
            var analogue = Resolve(row, parts, AnalogueBrandCode, AnalogueOemPartNumber);
            var typeText = row.Text(LinkTypeColumn)?.ToLowerInvariant();
            if (typeText is null || !AnalogueLinkTypes.All.Contains(typeText))
            {
                row.Error(LinkTypeColumn, "INVALID", "The link type is interchangeable or supersedes.");
            }

            var factor = row.Number(ScalingFactor) ?? 1m;
            var validFrom = today;
            if (file.Columns.Contains(ValidFrom) && row.Text(ValidFrom) is { } date
                && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out validFrom))
            {
                row.Error(ValidFrom, "INVALID", "Use the date format YYYY-MM-DD.");
            }

            var note = file.Columns.Contains(Note) ? row.Text(Note) : null;
            if (row.Errors.Count > 0 || part is null || analogue is null)
            {
                errors.AddRange(row.Errors);
                continue;
            }

            var type = PartAnalogueHandler.LinkType(typeText!);
            var candidate = PartAnalogue.Create(part.Value, analogue.Value, type, factor, validFrom, note, user.UserId);
            if (candidate.IsFailure)
            {
                errors.Add(new CatalogImportError(line, candidate.Error.Code == "SS502" ? AnalogueOemPartNumber : ScalingFactor, "RULE", candidate.Error.Description));
                continue;
            }

            var link = candidate.Value;
            var pair = (Math.Min(link.PartId, link.AnaloguePartId), Math.Max(link.PartId, link.AnaloguePartId));
            if (seenPairs.TryGetValue(pair, out var first))
            {
                errors.Add(new CatalogImportError(line, AnalogueOemPartNumber, "DUPLICATE", $"This pair is already on line {first}."));
                continue;
            }

            seenPairs[pair] = line;
            var existing = links.Find(l => Math.Min(l.PartId, l.AnaloguePartId) == pair.Item1 && Math.Max(l.PartId, l.AnaloguePartId) == pair.Item2);
            if (existing is not null)
            {
                if (existing.LinkType != link.LinkType || existing.PartId != link.PartId)
                {
                    errors.Add(new CatalogImportError(line, LinkTypeColumn, "RETYPE",
                        "These parts are linked with another type or direction; changing it needs a reason, so do it on the part's Analogues tab."));
                    continue;
                }

                var changed = existing.ChangeDetails(factor, validFrom, note);
                if (changed.IsFailure)
                {
                    errors.Add(new CatalogImportError(line, ScalingFactor, "INVALID", changed.Error.Description));
                }
                else if (changed.Value)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var rules = AnalogueRules.Check(new AnalogueLinkFact(0, link.PartId, link.AnaloguePartId, link.LinkType), facts);
            if (rules.IsFailure)
            {
                errors.Add(new CatalogImportError(line, AnalogueOemPartNumber, "RULE", rules.Error.Description));
                continue;
            }

            facts.Add(new AnalogueLinkFact(-line, link.PartId, link.AnaloguePartId, link.LinkType));
            db.Add(link);
            created++;
        }

        errors.Sort((a, b) => a.Line.CompareTo(b.Line));
        var withinGate = errors.Select(e => e.Line).Distinct().Count() <= file.Rows.Count * CatalogLimits.CsvErrorGate;
        if (withinGate)
        {
            // Saved even for a dry run, so the database's own rules answer the preview as they will the commit.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CatalogImportResult(dryRun, withinGate && !dryRun, file.Rows.Count, created, updated, unchanged, errors);
    }

    private static long? Resolve(CsvRow row, PartKeys parts, string brandColumn, string oemColumn)
    {
        var brand = row.Text(brandColumn)?.ToUpperInvariant();
        var oem = row.Text(oemColumn) is { } text ? Part.NormaliseCode(text) : null;
        if (brand is null || oem is not { IsSuccess: true })
        {
            row.Error(brand is null ? brandColumn : oemColumn, "REQUIRED", "Name the part by brand code and OEM part number.");
            return null;
        }

        if (!parts.ByKey.TryGetValue((brand, oem.Value), out var part))
        {
            row.Error(oemColumn, "UNKNOWN_REF", $"There is no part {oem.Value} of brand {brand}.");
            return null;
        }

        if (part.IsArchived)
        {
            row.Error(oemColumn, "ARCHIVED", $"The part {oem.Value} of brand {brand} is archived and cannot be linked.");
            return null;
        }

        return part.Id;
    }

    private async Task<PartKeys> PartKeysAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Set<Part>().AsNoTracking()
            .Select(p => new PartKey(p.Id, p.Brand!.Code, p.Code, p.IsArchived))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PartKeys(rows.ToDictionary(p => p.Id), rows.ToDictionary(p => (p.Brand, p.Oem)));
    }

    private static string TypeName(AnalogueLinkType type) =>
        type == AnalogueLinkType.Supersedes ? AnalogueLinkTypes.Supersedes : AnalogueLinkTypes.Interchangeable;

    private sealed record PartKey(long Id, string Brand, string Oem, bool IsArchived);

    private sealed record PartKeys(Dictionary<long, PartKey> ById, Dictionary<(string Brand, string Oem), PartKey> ByKey);
}

/// <summary>The CSV endpoints of part analogues, in the catalogue CSV shape (template, export, import with a dry run).</summary>
internal static class PartAnalogueCsvEndpoints
{
    private static readonly string ColumnList = string.Join(", ", PartAnalogueCsv.AllColumns);

    public static void Map(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/part-analogues/csv/template", () => File(PartAnalogueCsv.Template(), "part-analogues-template.csv"))
            .RequireAuthorization(Permissions.Inventory.Part.View)
            .WithTags("Parts")
            .WithName("GetPartAnalogueCsvTemplate")
            .WithSummary("Downloads the CSV template of part analogue links.")
            .WithDescription($"UTF-8 CSV with the header ({ColumnList}) and one example row; valid_from and note are optional columns. Permission Inventory.Part.View.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        v1.MapGet("/part-analogues/csv", async (HttpContext http, CancellationToken cancellationToken) =>
                File(await http.RequestServices.GetRequiredService<PartAnalogueCsv>().ExportAsync(cancellationToken).ConfigureAwait(false), "part-analogues.csv"))
            .RequireAuthorization(Permissions.Inventory.Part.View)
            .WithTags("Parts")
            .WithName("ExportPartAnalogues")
            .WithSummary("Exports every part analogue link as CSV.")
            .WithDescription($"Every link by brand and OEM number, with the template's columns ({ColumnList}); the file imports back without changes. Permission Inventory.Part.View.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        v1.MapPost("/part-analogues/csv", async (
                HttpContext http,
                [Description("The CSV file, UTF-8, at most 10 MB and 50,000 rows.")] IFormFile file,
                CancellationToken cancellationToken,
                [Description("true runs the whole import and rolls it back: the answer is what the import would do.")] bool dryRun = false) =>
            {
                if (file.Length > CatalogLimits.MaxCsvBytes)
                {
                    return Error.Validation("MasterData.CsvTooLarge", "The file is larger than 10 MB.").ToProblem();
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                string text;
                try
                {
                    text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    return Error.Validation("MasterData.CsvEncoding", "The file is not UTF-8.").ToProblem();
                }

                return (await http.RequestServices.GetRequiredService<PartAnalogueCsv>().ImportAsync(text, dryRun, cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(Permissions.Inventory.Part.Edit)
            .RequireRateLimiting(CatalogCsvEndpoints.RateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(CatalogLimits.MaxCsvBytes + (64 * 1024)))
            .DisableAntiforgery()
            .WithTags("Parts")
            .WithName("ImportPartAnalogues")
            .WithSummary("Imports part analogue links from CSV, or previews the import.")
            .WithDescription(
                $"Multipart form with file: UTF-8 CSV, comma or semicolon separated, header {ColumnList} in any order (valid_from and note optional). "
                + "Each row names a pair by brand and OEM number and creates the link, or updates the factor, date and note of the same link; rows equal to "
                + "the stored link are counted, not written. Row errors name the line, the column and the rule: UNKNOWN_REF and ARCHIVED parts, INVALID "
                + "values, DUPLICATE pairs in the file, RULE (self-link, a second link for a pair, a second successor, a supersession cycle; checked against "
                + "the existing links and the rows before), RETYPE (an import never changes a link's type or direction, nor removes one: that needs the "
                + "user's reason on the screen, ADR 0027). If more than 5 % of the rows have errors nothing is written (200, applied false). With "
                + "dryRun=true the import runs in full and is rolled back. At most 10 MB, 50,000 rows and 5 imports a minute. Permission Inventory.Part.Edit.")
            .Produces<CatalogImportResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseExample(StatusCodes.Status200OK, new CatalogImportResult(true, false, 12, 9, 1, 1,
                [new CatalogImportError(7, PartAnalogueCsv.AnalogueOemPartNumber, "RULE", AnalogueRules.SecondSuccessor.Description)]));
    }

    private static IResult File(string text, string name) =>
        Results.File(Encoding.UTF8.GetBytes(text), "text/csv; charset=utf-8", name);
}
