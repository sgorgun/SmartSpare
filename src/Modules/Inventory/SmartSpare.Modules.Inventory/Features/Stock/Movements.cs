using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Stock;

/// <summary>The stock ledger read model: movements newest first, with their source document.</summary>
internal static class Movements
{
    private const string From =
        """
        FROM inventory.stock_movement m
        JOIN inventory.warehouse w ON w.id = m.warehouse_id
        JOIN catalog.part p ON p.id = m.part_id
        JOIN ref.movement_type mt ON mt.code = m.movement_type_code
        LEFT JOIN inventory.transfer_order_line l ON l.id = m.transfer_order_line_id
        LEFT JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
        WHERE (@warehouse IS NULL OR m.warehouse_id = @warehouse)
          AND (@part IS NULL OR m.part_id = @part)
          AND (@type IS NULL OR m.movement_type_code = @type)
          AND (@from IS NULL OR m.occurred_at >= @from)
          AND (@to IS NULL OR m.occurred_at < @to)
        """;

    private const string PageSql =
        """
        SELECT m.id, m.occurred_at, m.warehouse_id, w.code AS warehouse_code, m.part_id, p.oem_part_number,
               m.movement_type_code AS movement_type, mt.balance_sign * m.quantity AS quantity,
               coalesce(o.number, m.external_doc_ref,
                        CASE WHEN m.import_batch_id IS NOT NULL THEN 'Import ' || m.import_batch_id END,
                        CASE WHEN m.service_order_line_id IS NOT NULL THEN 'Service order line ' || m.service_order_line_id END,
                        CASE WHEN m.purchase_order_line_id IS NOT NULL THEN 'Purchase order line ' || m.purchase_order_line_id END) AS document,
               m.note

        """ + From + "\nORDER BY m.occurred_at DESC, m.id DESC\nLIMIT @limit OFFSET @offset";

    private const string CountSql = "SELECT count(*)::int AS \"Value\"\n" + From;

    public sealed record Filter(short? WarehouseId, long? PartId, string? MovementType, DateTimeOffset? From, DateTimeOffset? To);

    public static Task<int> CountAsync(InventoryDbContext db, Filter filter, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(CountSql, Parameters(filter)).SingleAsync(cancellationToken);

    public static async Task<IReadOnlyList<MovementRow>> PageAsync(
        InventoryDbContext db, Filter filter, int page, int pageSize, CancellationToken cancellationToken) =>
        await db.Database
            .SqlQueryRaw<MovementRow>(
                PageSql,
                [.. Parameters(filter), new NpgsqlParameter<int>("limit", pageSize), new NpgsqlParameter<int>("offset", (page - 1) * pageSize)])
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static NpgsqlParameter[] Parameters(Filter filter) =>
    [
        new("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
        new("part", NpgsqlDbType.Bigint) { Value = (object?)filter.PartId ?? DBNull.Value },
        new("type", NpgsqlDbType.Text) { Value = (object?)filter.MovementType ?? DBNull.Value },
        new("from", NpgsqlDbType.TimestampTz) { Value = (object?)filter.From?.ToUniversalTime() ?? DBNull.Value },
        new("to", NpgsqlDbType.TimestampTz) { Value = (object?)filter.To?.ToUniversalTime() ?? DBNull.Value },
    ];
}

/// <summary>Ledger movements filtered by hub, part, type and time, newest first.</summary>
internal sealed record ListMovementsQuery(
    short? WarehouseId, long? PartId, string? MovementType, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
    : IQuery<PagedResponse<MovementRow>>;

internal sealed class ListMovementsQueryValidator : AbstractValidator<ListMovementsQuery>
{
    public ListMovementsQueryValidator()
    {
        RuleFor(q => q.MovementType).MaximumLength(20);
        RuleFor(q => q.To).GreaterThan(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("'To' must be after 'From'.");
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, MovementSearch.MaxPageSize);
    }
}

internal sealed class ListMovementsQueryHandler(InventoryDbContext db, TimeProvider clock)
    : IQueryHandler<ListMovementsQuery, PagedResponse<MovementRow>>
{
    public async Task<Result<PagedResponse<MovementRow>>> Handle(ListMovementsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A lower bound keeps the query on recent partitions of the ledger (partition pruning by occurred_at).
        var from = query.From ?? clock.GetUtcNow().AddDays(-MovementSearch.DefaultDays);
        var filter = new Movements.Filter(query.WarehouseId, query.PartId, query.MovementType, from, query.To);
        var total = await Movements.CountAsync(db, filter, cancellationToken).ConfigureAwait(false);
        var items = await Movements.PageAsync(db, filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResponse<MovementRow>(items, query.Page, query.PageSize, total));
    }
}

internal static class ListMovementsEndpoint
{
    /// <summary>GET /api/v1/inventory/movements — the stock ledger.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/movements", async (
                IQueryHandler<ListMovementsQuery, PagedResponse<MovementRow>> handler,
                CancellationToken cancellationToken,
                [Description("Hub id; empty for every hub.")] short? warehouseId = null,
                [Description("Part id; empty for every part.")] long? partId = null,
                [Description("Movement type code, for example TRANSFER_OUT or ADJUSTMENT_IN.")] string? type = null,
                [Description("Earliest time, inclusive (ISO 8601). Default: 90 days ago.")] DateTimeOffset? from = null,
                [Description("Latest time, exclusive (ISO 8601).")] DateTimeOffset? to = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Movements per page, 1 to 200.")] int pageSize = MovementSearch.DefaultPageSize) =>
            {
                var result = await handler.Handle(new ListMovementsQuery(warehouseId, partId, type, from, to, page, pageSize), cancellationToken)
                    .ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("ListMovements")
            .WithSummary("Lists stock ledger movements, newest first.")
            .WithDescription(
                "The append-only stock ledger with the signed effect of each movement on the on-hand stock (imported "
                + "historical demand has no effect) and its source document: the transfer order number, an external "
                + "reference, an import batch, a service or purchase order line. Without from, the last 90 days are listed. "
                + "Policy ReadInventory.")
            .Produces<PagedResponse<MovementRow>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<MovementRow>(
                [InventoryExamples.Movement], Page: 1, PageSize: MovementSearch.DefaultPageSize, TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem("To", "'To' must be after 'From'."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);
}
