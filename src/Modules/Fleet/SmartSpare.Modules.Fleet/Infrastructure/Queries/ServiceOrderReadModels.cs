using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Features.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Infrastructure.Queries;

/// <summary>The service order read side (STAGE_6_PLAN 3.3): lists by raw SQL, details from the aggregate plus lookups.</summary>
internal sealed class ServiceOrderReader(FleetDbContext db, FleetDueReader due, ServiceOrderAccess access)
{
    private const string ListSql =
        """
        SELECT so.id, so.number, so.machine_id, m.vin, mm.model_name, mp.name AS plan_name, so.warehouse_id, so.status::text AS status,
               so.due_engine_hours, so.created_at, so.completed_at, so.kit_reserved_at,
               (SELECT count(*)::int FROM fleet.service_order_line l WHERE l.service_order_id = so.id) AS line_count,
               (SELECT count(*)::int FROM fleet.service_order_line l WHERE l.service_order_id = so.id AND l.qty_required - l.qty_issued - l.qty_reserved > 0) AS short_lines,
               (SELECT count(*)::int FROM fleet.service_order_line l WHERE l.service_order_id = so.id AND l.qty_reserved > 0) AS held_lines
        FROM fleet.service_order so
        JOIN fleet.machine m ON m.id = so.machine_id
        JOIN fleet.machine_model mm ON mm.id = m.model_id
        LEFT JOIN fleet.maintenance_plan mp ON mp.id = so.maintenance_plan_id
        WHERE (@id IS NULL OR so.id = @id)
          AND (@status IS NULL OR so.status::text = @status)
          AND (@warehouse IS NULL OR so.warehouse_id = @warehouse)
          AND (@machine IS NULL OR so.machine_id = @machine)
        ORDER BY so.created_at DESC, so.id DESC
        """;

    public async Task<PagedResponse<ServiceOrderSummary>> ListAsync(
        string? status, short? warehouseId, long? machineId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(null, status, warehouseId, machineId, cancellationToken).ConfigureAwait(false);
        var code = await due.WarehouseCodesAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResponse<ServiceOrderSummary>(
            [.. rows.Skip((page - 1) * pageSize).Take(pageSize).Select(r => r.ToSummary(code))], page, pageSize, rows.Count);
    }

    public async Task<ServiceOrderDetails?> DetailsAsync(long id, CancellationToken cancellationToken)
    {
        var row = (await RowsAsync(id, null, null, null, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        var order = await db.Set<ServiceOrder>().AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false);
        var partIds = order.Lines.Select(l => l.PartId).ToList();
        var parts = await db.Set<PartSnapshot>().AsNoTracking().Where(p => partIds.Contains(p.PartId))
            .ToDictionaryAsync(p => p.PartId, cancellationToken).ConfigureAwait(false);
        var code = await due.WarehouseCodesAsync(cancellationToken).ConfigureAwait(false);

        return new ServiceOrderDetails(
            row.ToSummary(code), order.TriggerEngineHours, order.CompletedEngineHours, order.CancelReason, order.CreatedBy, order.KitReservedAt,
            [
                .. order.Lines.OrderBy(l => l.PartId).Select(l =>
                {
                    var part = parts.GetValueOrDefault(l.PartId);
                    return new ServiceOrderLineResponse(l.Id, l.PartId, part?.OemPartNumber, part?.Description, part?.UnitOfMeasure,
                        l.QtyRequired, l.QtyReserved, l.QtyIssued, l.QtyMissing);
                }),
            ],
            access.AllowedActions(order));
    }

    private async Task<List<Row>> RowsAsync(long? id, string? status, short? warehouseId, long? machineId, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<Row>(
                ListSql,
                new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = (object?)id ?? DBNull.Value },
                new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)ServiceOrderMapping.SqlStatus(status) ?? DBNull.Value },
                new NpgsqlParameter("warehouse", NpgsqlDbType.Smallint) { Value = (object?)warehouseId ?? DBNull.Value },
                new NpgsqlParameter("machine", NpgsqlDbType.Bigint) { Value = (object?)machineId ?? DBNull.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    internal sealed record Row(
        long Id,
        string Number,
        long MachineId,
        string Vin,
        string ModelName,
        string? PlanName,
        short WarehouseId,
        string Status,
        decimal? DueEngineHours,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? KitReservedAt,
        int LineCount,
        int ShortLines,
        int HeldLines)
    {
        public ServiceOrderSummary ToSummary(Func<short, string> warehouseCode) => new(
            Id, Number, MachineId, Vin, ModelName, PlanName, WarehouseId, warehouseCode(WarehouseId), ServiceOrderMapping.ApiStatus(Status),
            ServiceOrderMapping.KitStatusName(ServiceOrderMapping.KitOf(Status, KitReservedAt, LineCount, ShortLines, HeldLines)),
            DueEngineHours, CreatedAt, CompletedAt);
    }
}

internal sealed class ListServiceOrdersQueryHandler(ServiceOrderReader reader) : IQueryHandler<ListServiceOrdersQuery, PagedResponse<ServiceOrderSummary>>
{
    public async Task<Result<PagedResponse<ServiceOrderSummary>>> Handle(ListServiceOrdersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await reader.ListAsync(query.Status, query.WarehouseId, query.MachineId, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class GetServiceOrderQueryHandler(ServiceOrderReader reader) : IQueryHandler<GetServiceOrderQuery, ServiceOrderDetails>
{
    public async Task<Result<ServiceOrderDetails>> Handle(GetServiceOrderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await reader.DetailsAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } details
            ? details
            : FleetErrors.ServiceOrderNotFound;
    }
}
