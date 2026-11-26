using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Fleet.Domain;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Features.Machines;
using SmartSpare.Modules.Fleet.Features.Maintenance;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Infrastructure.Queries;

/// <summary>Machine rows of the Fleet read side (STAGE_6_PLAN 3.3): module-local tables plus Platform reference data.</summary>
internal static class MachineRows
{
    private const string Sql =
        """
        SELECT m.id, m.vin, mm.model_name, b.code AS brand_code, t.code AS machine_type, c.customer_code,
               m.home_warehouse_id, m.current_engine_hours, m.engine_hours_read_at, m.status::text AS machine_status,
               m.manufacture_year, m.commissioned_on, oc.code AS operating_condition, oc.interval_factor
        FROM fleet.machine m
        JOIN fleet.machine_model mm ON mm.id = m.model_id
        JOIN ref.brand b ON b.id = mm.brand_id
        JOIN ref.machine_type t ON t.id = mm.machine_type_id
        JOIN fleet.customer c ON c.id = m.customer_id
        JOIN ref.operating_condition oc ON oc.id = m.operating_condition_id
        WHERE (@id IS NULL OR m.id = @id)
          AND (@warehouse IS NULL OR m.home_warehouse_id = @warehouse)
          AND (@status IS NULL OR m.status::text = @status)
          AND (@pattern IS NULL OR m.vin ILIKE @pattern)
        ORDER BY m.vin
        """;

    public sealed record Filter(long? Id = null, short? WarehouseId = null, string? Status = null, string? Search = null);

    public static async Task<IReadOnlyList<Row>> LoadAsync(FleetDbContext db, Filter filter, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<Row>(
                Sql,
                new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = (object?)filter.Id ?? DBNull.Value },
                new NpgsqlParameter("warehouse", NpgsqlDbType.Smallint) { Value = (object?)filter.WarehouseId ?? DBNull.Value },
                new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)filter.Status ?? DBNull.Value },
                new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = Pattern(filter.Search) ?? (object)DBNull.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary><c>%text%</c> with LIKE wildcards escaped; null for an empty search.</summary>
    private static string? Pattern(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? null
            : "%" + search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    internal sealed record Row(
        long Id,
        string Vin,
        string ModelName,
        string BrandCode,
        string MachineType,
        string CustomerCode,
        short HomeWarehouseId,
        decimal CurrentEngineHours,
        DateTimeOffset? EngineHoursReadAt,
        string MachineStatus,
        short ManufactureYear,
        DateOnly? CommissionedOn,
        string OperatingCondition,
        decimal IntervalFactor);
}

/// <summary>Computes the due calculation of every plan of the given machines and maps it to the API.</summary>
internal sealed class FleetDueReader(FleetDbContext db, IWarehouseQueries warehouses, TimeProvider time)
{
    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Plans per machine id, each mapped with <see cref="MaintenanceDueMapping"/>, most urgent first.</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<(MachinePlanDueInput Input, MaintenancePlanDue Due)>>> PlansAsync(
        IReadOnlyCollection<long>? machineIds, short? warehouseId, int horizonDays, CancellationToken cancellationToken)
    {
        var inputs = await MaintenanceDueInputs.LoadAsync(db, machineIds, warehouseId, cancellationToken).ConfigureAwait(false);
        var today = Today;
        return inputs
            .Select(i => (Input: i, Due: MaintenanceDueMapping.ToDto(i.MaintenancePlanId, i.PlanName, i.Input,
                MaintenanceDueCalculator.Calculate(i.Input, today, horizonDays))))
            .GroupBy(x => x.Input.MachineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(MachinePlanDueInput, MaintenancePlanDue)>)[.. MaintenanceDueMapping.ByUrgency(g, x => x.Due)]);
    }

    /// <summary>Hub codes by id, from Inventory (in process or over its internal API).</summary>
    public async Task<Func<short, string>> WarehouseCodesAsync(CancellationToken cancellationToken)
    {
        var codes = (await warehouses.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(w => w.Id, w => w.Code);
        return id => codes.TryGetValue(id, out var code) ? code : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static MachineSummary Summary(MachineRows.Row m, Func<short, string> warehouseCode, MaintenancePlanDue? next) => new(
        m.Id, m.Vin, m.ModelName, m.BrandCode, m.MachineType, m.CustomerCode, m.HomeWarehouseId, warehouseCode(m.HomeWarehouseId),
        m.CurrentEngineHours, m.EngineHoursReadAt, m.MachineStatus, next);
}

internal sealed class ListMachinesQueryHandler(FleetDbContext db, FleetDueReader due) : IQueryHandler<ListMachinesQuery, PagedResponse<MachineSummary>>
{
    public async Task<Result<PagedResponse<MachineSummary>>> Handle(ListMachinesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var machines = await MachineRows.LoadAsync(db, new MachineRows.Filter(null, query.WarehouseId, query.Status, query.Search), cancellationToken)
            .ConfigureAwait(false);
        var plans = await due.PlansAsync([.. machines.Select(m => m.Id)], query.WarehouseId, MaintenanceDueCalculator.DefaultPlanningHorizonDays, cancellationToken)
            .ConfigureAwait(false);
        var code = await due.WarehouseCodesAsync(cancellationToken).ConfigureAwait(false);

        var rows = machines
            .Select(m => FleetDueReader.Summary(m, code, plans.TryGetValue(m.Id, out var p) && p.Count > 0 ? p[0].Due : null))
            .Where(m => query.DueStatus is null || m.NextService?.Status == query.DueStatus)
            .OrderBy(m => m.NextService is null ? int.MaxValue : MaintenanceDueMapping.Urgency(m.NextService.Status))
            .ThenBy(m => m.NextService?.DueDate ?? DateOnly.MaxValue)
            .ThenBy(m => m.Vin, StringComparer.Ordinal)
            .ToList();
        return new PagedResponse<MachineSummary>(
            [.. rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)], query.Page, query.PageSize, rows.Count);
    }
}

internal sealed class GetMachineQueryHandler(FleetDbContext db, FleetDueReader due) : IQueryHandler<GetMachineQuery, MachineDetails>
{
    public const int RecentReadings = 5;

    public async Task<Result<MachineDetails>> Handle(GetMachineQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var machine = (await MachineRows.LoadAsync(db, new MachineRows.Filter(query.Id), cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        if (machine is null)
        {
            return FleetErrors.MachineNotFound;
        }

        var plans = (await due.PlansAsync([machine.Id], null, MaintenanceDueCalculator.DefaultPlanningHorizonDays, cancellationToken)
                .ConfigureAwait(false))
            .GetValueOrDefault(machine.Id, []);
        var code = await due.WarehouseCodesAsync(cancellationToken).ConfigureAwait(false);
        var readings = await Readings.PageAsync(db, machine.Id, 1, RecentReadings, cancellationToken).ConfigureAwait(false);
        var usage = plans.Count > 0 ? UsageRate.From(plans[0].Input.Input.Readings).HoursPerDay : null;

        return new MachineDetails(
            FleetDueReader.Summary(machine, code, plans.Count > 0 ? plans[0].Due : null),
            machine.ManufactureYear, machine.CommissionedOn, machine.OperatingCondition, machine.IntervalFactor,
            usage is null ? null : decimal.Round(usage.Value, 2, MidpointRounding.AwayFromZero),
            [.. plans.Select(p => p.Due)], readings);
    }
}

/// <summary>Reading rows, newest first.</summary>
internal static class Readings
{
    public static async Task<IReadOnlyList<ReadingRow>> PageAsync(FleetDbContext db, long machineId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(
                """
                SELECT r.id, r.read_at, r.reading_hours, r.source::text AS source, r.recorded_by
                FROM fleet.engine_hours_reading r
                WHERE r.machine_id = @machine
                ORDER BY r.read_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new NpgsqlParameter<long>("machine", machineId),
                new NpgsqlParameter<int>("limit", pageSize),
                new NpgsqlParameter<int>("offset", (page - 1) * pageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new ReadingRow(r.Id, r.ReadAt, r.ReadingHours, SourceName(r.Source), r.RecordedBy))];
    }

    public static Task<int> CountAsync(FleetDbContext db, long machineId, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM fleet.engine_hours_reading WHERE machine_id = @machine",
                new NpgsqlParameter<long>("machine", machineId))
            .SingleAsync(cancellationToken);

    /// <summary><c>service_visit</c> → <c>serviceVisit</c>.</summary>
    private static string SourceName(string source) => source == "service_visit" ? ReadingSources.ServiceVisit : source;

    internal sealed record Row(long Id, DateTimeOffset ReadAt, decimal ReadingHours, string Source, Guid? RecordedBy);
}

internal sealed class ListReadingsQueryHandler(FleetDbContext db) : IQueryHandler<ListReadingsQuery, PagedResponse<ReadingRow>>
{
    public async Task<Result<PagedResponse<ReadingRow>>> Handle(ListReadingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!await db.Set<Domain.Machines.Machine>().AnyAsync(m => m.Id == query.MachineId, cancellationToken).ConfigureAwait(false))
        {
            return FleetErrors.MachineNotFound;
        }

        var total = await Readings.CountAsync(db, query.MachineId, cancellationToken).ConfigureAwait(false);
        var items = await Readings.PageAsync(db, query.MachineId, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<ReadingRow>(items, query.Page, query.PageSize, total);
    }
}

internal sealed class DueMaintenanceQueryHandler(FleetDbContext db, FleetDueReader due) : IQueryHandler<DueMaintenanceQuery, PagedResponse<DueMaintenanceRow>>
{
    public async Task<Result<PagedResponse<DueMaintenanceRow>>> Handle(DueMaintenanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var machines = (await MachineRows.LoadAsync(db, new MachineRows.Filter(null, query.WarehouseId, "active"), cancellationToken).ConfigureAwait(false))
            .ToDictionary(m => m.Id);
        var plans = await due.PlansAsync(null, query.WarehouseId, query.HorizonDays, cancellationToken).ConfigureAwait(false);
        var code = await due.WarehouseCodesAsync(cancellationToken).ConfigureAwait(false);
        var listed = MaintenanceDueMapping.ListedStatuses(query.Status);

        var rows = MaintenanceDueMapping.ByDueDate(
                plans.Values.SelectMany(p => p)
                    .Where(p => listed.Contains(p.Due.Status) && machines.ContainsKey(p.Input.MachineId))
                    .Select(p =>
                    {
                        var m = machines[p.Input.MachineId];
                        return new DueMaintenanceRow(m.Id, m.Vin, m.ModelName, m.BrandCode, m.CustomerCode, m.HomeWarehouseId,
                            code(m.HomeWarehouseId), m.CurrentEngineHours, p.Due);
                    }),
                r => r.Due)
            .ThenBy(r => r.Vin, StringComparer.Ordinal)
            .ToList();
        return new PagedResponse<DueMaintenanceRow>(
            [.. rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)], query.Page, query.PageSize, rows.Count);
    }
}

internal sealed class ListMaintenancePlansQueryHandler(FleetDbContext db) : IQueryHandler<ListMaintenancePlansQuery, IReadOnlyList<MaintenancePlanResponse>>
{
    public async Task<Result<IReadOnlyList<MaintenancePlanResponse>>> Handle(ListMaintenancePlansQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = await db.Database.SqlQueryRaw<Row>(
                """
                SELECT mp.id, mp.model_id, mm.model_name, b.code AS brand_code, mp.name, mp.interval_hours, mp.interval_months,
                       k.part_id, s.oem_part_number, s.description, s.unit_of_measure, k.quantity
                FROM fleet.maintenance_plan mp
                JOIN fleet.machine_model mm ON mm.id = mp.model_id
                JOIN ref.brand b ON b.id = mm.brand_id
                LEFT JOIN fleet.maintenance_plan_part k ON k.maintenance_plan_id = mp.id
                LEFT JOIN fleet.part_snapshot s ON s.part_id = k.part_id
                WHERE mp.is_active AND (@model IS NULL OR mp.model_id = @model)
                ORDER BY b.code, mm.model_name, mp.interval_hours, s.oem_part_number, k.part_id
                """,
                new NpgsqlParameter("model", NpgsqlDbType.Integer) { Value = (object?)query.ModelId ?? DBNull.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<MaintenancePlanResponse> plans =
        [
            .. rows.GroupBy(r => r.Id).Select(g =>
            {
                var p = g.First();
                return new MaintenancePlanResponse(p.Id, p.ModelId, p.ModelName, p.BrandCode, p.Name, p.IntervalHours, p.IntervalMonths,
                    [.. g.Where(r => r.PartId is not null).Select(r => new KitPart(r.PartId!.Value, r.OemPartNumber, r.Description, r.UnitOfMeasure, r.Quantity!.Value))]);
            }),
        ];
        return Result.Success(plans);
    }

    internal sealed record Row(
        int Id,
        int ModelId,
        string ModelName,
        string BrandCode,
        string Name,
        int IntervalHours,
        short? IntervalMonths,
        long? PartId,
        string? OemPartNumber,
        string? Description,
        string? UnitOfMeasure,
        decimal? Quantity);
}
