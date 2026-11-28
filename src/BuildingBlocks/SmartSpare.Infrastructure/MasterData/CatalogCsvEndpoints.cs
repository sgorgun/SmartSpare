using System.ComponentModel;
using System.Numerics;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Http;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// The CSV endpoints of a catalogue with a <see cref="CatalogCsv{TDto, TWrite}"/> (STAGE_7_PLAN 2.6): the template, the
/// export of the active records and the import with its dry run.
/// </summary>
public static class CatalogCsvEndpoints
{
    /// <summary>Rate limit of catalogue imports: per user, else per client address (as the parts import).</summary>
    public const string RateLimitPolicy = "catalog-csv-import";

    public static readonly Error NotUtf8 = Error.Validation("MasterData.CsvEncoding", "The file must be UTF-8 text.");

    public static readonly Error TooLarge = Error.Validation("MasterData.CsvTooLarge", "A file has at most 10 MB.");

    /// <summary>Registers the import rate limit once, however many modules call it.</summary>
    public static IServiceCollection AddCatalogCsvRateLimit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(s => s.ServiceType == typeof(IRateLimitMarker)))
        {
            return services;
        }

        services.AddSingleton<IRateLimitMarker>(_ => null!);
        return services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirst(AppClaimTypes.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = CatalogLimits.CsvImportsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
    }

    internal static void Map<TContext, TRecord, TId, TDto, TWrite>(
        RouteGroupBuilder group, CatalogDefinition<TRecord, TId, TDto, TWrite> d,
        Func<HttpContext, CatalogService<TContext, TRecord, TId, TDto, TWrite>> service)
        where TContext : DbContext
        where TRecord : MasterRecord<TId>
        where TId : struct, INumber<TId>
        where TDto : notnull
        where TWrite : ICatalogWrite
    {
        var csv = d.Csv!;
        var slug = d.Route.Trim('/');
        var columns = string.Join(", ", csv.Columns);

        group.MapGet("/csv/template", (HttpContext http) => File(service(http).CsvTemplate(), $"{slug}-template.csv"))
            .RequireAuthorization(d.ViewPermission)
            .WithName($"Get{d.OperationNoun}CsvTemplate")
            .WithSummary($"Downloads the CSV template of {d.Noun}s.")
            .WithDescription($"UTF-8 CSV with the header ({columns}) and one example row. {d.Rules}")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/csv", async (HttpContext http, CancellationToken cancellationToken) =>
                File(await service(http).ExportCsvAsync(cancellationToken).ConfigureAwait(false), $"{slug}.csv"))
            .RequireAuthorization(d.ViewPermission)
            .WithName($"Export{d.OperationNoun}s")
            .WithSummary($"Exports the active {d.Noun}s as CSV.")
            .WithDescription($"Every active {d.Noun} by code, with the template's columns ({columns}); the file imports back without changes. {d.Rules}")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/csv", async (
                HttpContext http,
                [Description("The CSV file, UTF-8, at most 10 MB and 50,000 rows.")] IFormFile file,
                CancellationToken cancellationToken,
                [Description("true runs the whole import and rolls it back: the answer is what the import would do.")] bool dryRun = false) =>
            {
                // The endpoint's policy is the edit permission; an import also creates, so it needs the create permission too.
                if (!(await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, d.CreatePermission).ConfigureAwait(false)).Succeeded)
                {
                    return Results.Forbid();
                }

                if (file.Length > CatalogLimits.MaxCsvBytes)
                {
                    return TooLarge.ToProblem();
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
                    return NotUtf8.ToProblem();
                }

                return (await service(http).ImportCsvAsync(text, dryRun, cancellationToken).ConfigureAwait(false)).ToHttpResult();
            })
            .RequireAuthorization(d.EditPermission)
            .RequireRateLimiting(RateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(CatalogLimits.MaxCsvBytes + (64 * 1024)))
            .DisableAntiforgery()
            .WithName($"Import{d.OperationNoun}s")
            .WithSummary($"Imports {d.Noun}s from CSV, or previews the import.")
            .WithDescription(
                $"Multipart form with file: UTF-8 CSV, comma or semicolon separated, header {columns} in any order. Each row "
                + "creates the record with its code or updates it (upsert by code); rows equal to the stored record are counted, "
                + "not written. A code twice in the file is a DUPLICATE error on the later row, an archived code an ARCHIVED "
                + "error (an import never archives or restores), and each row is checked like the same create or update over "
                + "the API (INVALID). If more than 5 % of the rows have errors nothing is written (200, applied false). With "
                + "dryRun=true the import runs in full and is rolled back, so the preview is exactly what the import does. "
                + $"Needs both the create and the edit permission; at most 10 MB, 50,000 rows and 5 imports a minute. {d.Rules}")
            .Produces<CatalogImportResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithResponseExample(StatusCodes.Status200OK, new CatalogImportResult(true, false, 40, 3, 2, 34,
                [new CatalogImportError(7, "code", "DUPLICATE", $"The code {csv.ExampleRow[0]} is already on line 3.")]))
            .WithResponseExample(StatusCodes.Status400BadRequest, OpenApiConventions.Problem(Error.Validation(
                "MasterData.CsvHeader", $"The header must name exactly these columns: {columns}. Missing: {csv.Columns[^1]}.")));
    }

    private static IResult File(string text, string name) =>
        Results.File(Encoding.UTF8.GetBytes(text), "text/csv; charset=utf-8", name);

    private interface IRateLimitMarker;
}
