using System.ComponentModel;
using System.Numerics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// Builds the endpoints every catalogue has (ADR 0025, STAGE_7_PLAN 2.2) from its <see cref="CatalogDefinition{TRecord, TId, TDto, TWrite}"/>:
/// list with search, archived switch, sort and paging; detail with <c>ETag</c>; lookup of active entries; create;
/// update, archive and restore with <c>If-Match</c>; and the change history. Each is documented for OpenAPI with
/// summary, description, parameters, response types and examples (ADR 0019).
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalog<TContext, TRecord, TId, TDto, TWrite>(
        this IEndpointRouteBuilder module, CatalogDefinition<TRecord, TId, TDto, TWrite> definition)
        where TContext : DbContext
        where TRecord : MasterRecord<TId>
        where TId : struct, INumber<TId>
        where TDto : notnull
        where TWrite : ICatalogWrite
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(definition);
        var d = definition;
        var group = module.MapGroup(d.Route).WithTags(d.Tag);
        var notFound = OpenApiConventions.Problem(CatalogErrors.NotFound);
        var stale = OpenApiConventions.Problem(Error.Conflict("Database.ConcurrencyConflict", "The record was changed by someone else. Reload it and try again."));

        // The collection routes go on the module's builder: a group's empty pattern would end in a slash.
        module.MapGet(d.Route, async (
                HttpContext http, CancellationToken cancellationToken,
                [Description("Text contained in the code or the name.")] string? search = null,
                [Description("true lists archived records instead of active ones.")] bool archived = false,
                [Description("code (default), -code, name or -name.")] string? sort = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Records per page, 1 to 100.")] int pageSize = 25) =>
                (await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).ListAsync(search?[..Math.Min(search.Length, CatalogLimits.MaxSearchLength)], archived, sort, page, pageSize, cancellationToken)
                    .ConfigureAwait(false)).ToHttpResult())
            .WithTags(d.Tag)
            .RequireAuthorization(d.ViewPermission)
            .WithName($"List{d.OperationNoun}s")
            .WithSummary($"Lists {d.Noun}s.")
            .WithDescription($"Active (or archived) {d.Noun}s whose code or name contains the search text, sorted and paged. {d.Rules}")
            .Produces<PagedResponse<TDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<TDto>([d.Example], 1, 25, 1));

        group.MapGet("/lookup", async (
                HttpContext http, CancellationToken cancellationToken,
                [Description("Text contained in the code or the name.")] string? search = null,
                [Description("At most this many entries, 1 to 50.")] int limit = CatalogLimits.DefaultLookupLimit) =>
                Results.Ok(await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).LookupAsync(search?[..Math.Min(search.Length, CatalogLimits.MaxSearchLength)], limit, cancellationToken).ConfigureAwait(false)))
            .RequireAuthorization(d.ViewPermission)
            .WithName($"Lookup{d.OperationNoun}s")
            .WithSummary($"Finds active {d.Noun}s for an autocomplete field.")
            .WithDescription($"Active {d.Noun}s only (archived ones cannot be chosen), by code, as code, name and a short detail.")
            .Produces<IReadOnlyList<CatalogLookupItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new[] { d.LookupExample });

        group.MapGet("/{id:long}", async ([Description("Record id.")] long id, HttpContext http, CancellationToken cancellationToken) =>
            {
                if (Key<TId>(id) is not { } key)
                {
                    return CatalogErrors.NotFound.ToProblem();
                }

                var result = await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).GetAsync(key, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? new ETagResult(TypedResults.Ok(result.Value), d.VersionOf(result.Value)) : result.Error.ToProblem();
            })
            .RequireAuthorization(d.ViewPermission)
            .WithName($"Get{d.OperationNoun}")
            .WithSummary($"Returns one {d.Noun}.")
            .WithDescription($"The {d.Noun}; the ETag header carries its version for If-Match.")
            .Produces<TDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, d.Example)
            .WithResponseExample(StatusCodes.Status404NotFound, notFound);

        group.MapGet("/{id:long}/history", async (
                [Description("Record id.")] long id, HttpContext http, CancellationToken cancellationToken,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Changes per page, 1 to 100.")] int pageSize = CatalogLimits.DefaultHistoryPageSize) =>
            {
                if (page < 1 || pageSize is < 1 or > CatalogLimits.MaxPageSize)
                {
                    return CatalogErrors.InvalidPaging.ToProblem();
                }

                if (Key<TId>(id) is not { } key || !await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).ExistsAsync(key, cancellationToken).ConfigureAwait(false))
                {
                    return CatalogErrors.NotFound.ToProblem();
                }

                var services = http.RequestServices;
                return Results.Ok(await RecordHistory.ReadAsync(
                        services.GetRequiredService<TContext>(), services.GetRequiredService<IActorNames>(), d.AuditTable,
                        key.ToString(null, System.Globalization.CultureInfo.InvariantCulture), page, pageSize, d.FieldLabels, cancellationToken)
                    .ConfigureAwait(false));
            })
            .RequireAuthorization(d.ViewPermission)
            .WithName($"Get{d.OperationNoun}History")
            .WithSummary($"Returns the change history of one {d.Noun}.")
            .WithDescription("Newest first: when, who (first name and initial; none for the system), what changed (field, old and new value) and the reason given.")
            .Produces<PagedResponse<RecordHistoryEntry>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<RecordHistoryEntry>(
                [new RecordHistoryEntry(new DateTimeOffset(2026, 12, 1, 9, 30, 0, TimeSpan.Zero), "changed", "Rasa K.", [new RecordChange("Name", "Damaged", "Damaged in storage")], null)],
                1, CatalogLimits.DefaultHistoryPageSize, 1));

        module.MapPost(d.Route, async (HttpContext http, CancellationToken cancellationToken) =>
            {
                if (await BodyAsync<TWrite>(http, cancellationToken).ConfigureAwait(false) is not { } body)
                {
                    return CatalogErrors.BodyRequired.ToProblem();
                }

                var result = await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).CreateAsync(body, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? new ETagResult(TypedResults.Created($"{d.ApiBase}{d.Route}/{IdOf(result.Value, d)}", result.Value), d.VersionOf(result.Value))
                    : result.Error.ToProblem();
            })
            .WithTags(d.Tag)
            .RequireAuthorization(d.CreatePermission)
            .WithName($"Create{d.OperationNoun}")
            .WithSummary($"Creates a {d.Noun}.")
            .WithDescription($"The code is stored in capitals and must be unused (409 Catalog.CodeTaken). {d.Rules}")
            .Accepts<TWrite>("application/json")
            .Produces<TDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(d.WriteExample)
            .WithResponseExample(StatusCodes.Status201Created, d.Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(CatalogErrors.CodeTaken));

        group.MapPut("/{id:long}", async (
                [Description("Record id.")] long id, [Description("The version read, as its ETag, e.g. \"8841\".")][FromHeader(Name = "If-Match")] string? ifMatch,
                HttpContext http, CancellationToken cancellationToken) =>
            {
                if (Key<TId>(id) is not { } key)
                {
                    return CatalogErrors.NotFound.ToProblem();
                }

                return await BodyAsync<TWrite>(http, cancellationToken).ConfigureAwait(false) is { } body
                    ? (await Service<TContext, TRecord, TId, TDto, TWrite>(http, d).UpdateAsync(key, body, Versions.Parse(ifMatch), cancellationToken).ConfigureAwait(false)).ToHttpResult()
                    : CatalogErrors.BodyRequired.ToProblem();
            })
            .RequireAuthorization(d.EditPermission)
            .WithName($"Update{d.OperationNoun}")
            .WithSummary($"Changes a {d.Noun}.")
            .WithDescription("Needs If-Match with the version read (missing: 400 Catalog.VersionRequired; stale: 409). The code stays as it is; "
                             + $"only an administrator may change it (422 Catalog.CodeFixed). {d.Rules}")
            .Accepts<TWrite>("application/json")
            .Produces<TDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(d.WriteExample)
            .WithResponseExample(StatusCodes.Status200OK, d.Example)
            .WithResponseExample(StatusCodes.Status409Conflict, stale)
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OpenApiConventions.Problem(CatalogErrors.CodeFixed));

        MapState<TContext, TRecord, TId, TDto, TWrite>(group, d, "archive", (s, key, version, ct) => s.ArchiveAsync(key, version, ct), $"Archive{d.OperationNoun}", $"Archives a {d.Noun}.",
                $"Needs If-Match. An archived {d.Noun} stays on past documents but cannot be chosen (422 SS500). Refused with the reasons while it is in use (409 SS501). {d.Rules}")
            .WithResponseExample(StatusCodes.Status409Conflict, OpenApiConventions.Problem(CatalogErrors.ArchiveBlocked(["on hand 12 at KLP"])));
        MapState<TContext, TRecord, TId, TDto, TWrite>(group, d, "restore", (s, key, version, ct) => s.RestoreAsync(key, version, ct), $"Restore{d.OperationNoun}", $"Restores an archived {d.Noun}.",
                $"Needs If-Match. The {d.Noun} can be chosen again. {d.Rules}")
            .WithResponseExample(StatusCodes.Status409Conflict, stale);
        if (d.Csv is not null)
        {
            CatalogCsvEndpoints.Map<TContext, TRecord, TId, TDto, TWrite>(group, d, http => Service<TContext, TRecord, TId, TDto, TWrite>(http, d));
        }

        return group;
    }

    private static RouteHandlerBuilder MapState<TContext, TRecord, TId, TDto, TWrite>(
        RouteGroupBuilder group, CatalogDefinition<TRecord, TId, TDto, TWrite> d, string action,
        Func<CatalogService<TContext, TRecord, TId, TDto, TWrite>, TId, uint?, CancellationToken, Task<Result<TDto>>> run,
        string name, string summary, string description)
        where TContext : DbContext
        where TRecord : MasterRecord<TId>
        where TId : struct, INumber<TId>
        where TDto : notnull
        where TWrite : ICatalogWrite =>
        group.MapPost($"/{{id:long}}/{action}", async (
                [Description("Record id.")] long id, [Description("The version read, as its ETag, e.g. \"8841\".")][FromHeader(Name = "If-Match")] string? ifMatch, HttpContext http, CancellationToken cancellationToken) =>
                Key<TId>(id) is { } key
                    ? (await run(Service<TContext, TRecord, TId, TDto, TWrite>(http, d), key, Versions.Parse(ifMatch), cancellationToken).ConfigureAwait(false)).ToHttpResult()
                    : CatalogErrors.NotFound.ToProblem())
            .RequireAuthorization(d.ArchivePermission)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description)
            .Produces<TDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseExample(StatusCodes.Status200OK, d.Example);

    private static CatalogService<TContext, TRecord, TId, TDto, TWrite> Service<TContext, TRecord, TId, TDto, TWrite>(
        HttpContext http, CatalogDefinition<TRecord, TId, TDto, TWrite> definition)
        where TContext : DbContext
        where TRecord : MasterRecord<TId>
        where TId : struct, INumber<TId>
        where TWrite : ICatalogWrite
    {
        var services = http.RequestServices;
        return new CatalogService<TContext, TRecord, TId, TDto, TWrite>(
            services.GetRequiredService<TContext>(), services.GetRequiredService<IUnitOfWork<TContext>>(), services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<TimeProvider>(), services, definition);
    }

    /// <summary>
    /// The JSON body as the catalogue's write type, or <see langword="null"/> if it is missing or malformed. Read by hand
    /// because the ASP.NET route-handler analyzer cannot inspect a body parameter of a generic type.
    /// </summary>
    private static async Task<TWrite?> BodyAsync<TWrite>(HttpContext http, CancellationToken cancellationToken)
        where TWrite : ICatalogWrite
    {
        if (!http.Request.HasJsonContentType())
        {
            return default;
        }

        try
        {
            return await http.Request.ReadFromJsonAsync<TWrite>(cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    /// <summary>The key of a route id, or <see langword="null"/> when it cannot be one (then the record does not exist).</summary>
    private static TId? Key<TId>(long id)
        where TId : struct, INumber<TId> =>
        id > 0 && TId.CreateSaturating(id) is var key && long.CreateChecked(key) == id ? key : null;

    private static string IdOf<TRecord, TId, TDto, TWrite>(TDto dto, CatalogDefinition<TRecord, TId, TDto, TWrite> definition)
        where TRecord : MasterRecord<TId>
        where TId : struct, INumber<TId>
        where TWrite : ICatalogWrite =>
        definition.IdOf(dto);

    /// <summary>Sets the <c>ETag</c> header of a successful read or create.</summary>
    private sealed class ETagResult(IResult inner, string version) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.ETag = $"\"{version}\"";
            return inner.ExecuteAsync(httpContext);
        }
    }
}
