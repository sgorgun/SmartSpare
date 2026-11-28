using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Domain.Responsibilities;

/// <summary>The record kinds of Inventory that have responsible employees (ADR 0026); values of <c>ref.assignment_role.record_kind</c>.</summary>
internal static class RecordKinds
{
    public const string Brand = "brand";

    public const string Warehouse = "warehouse";

    public const string Manufacturer = "manufacturer";

    public const string Part = "part";
}

/// <summary>An employee in an assignment role, e.g. KEEPER (a value; the employee belongs to the Identity module).</summary>
internal sealed record ResponsibleEmployee(Guid EmployeeId, string Role);

/// <summary>
/// A responsible employee of an Inventory record (STAGE_7_PLAN 3.7): "keeper of KLP", "brand manager of CLAAS". One table
/// per record kind with a real foreign key to the record; the employee is a plain id validated through the employee
/// directory. Several employees per record, several roles per employee, each pair once.
/// </summary>
internal interface IResponsibility
{
    Guid EmployeeId { get; }

    string Role { get; }
}

/// <summary>A responsible employee of a brand (<c>catalog.brand_responsible</c>).</summary>
internal sealed class BrandResponsible : Entity<long>, IResponsibility
{
    private BrandResponsible()
        : base(default) => Role = null!;

    public BrandResponsible(short brandId, Guid employeeId, string role, Guid? createdBy)
        : base(default)
    {
        BrandId = brandId;
        EmployeeId = employeeId;
        Role = role;
        CreatedBy = createdBy;
    }

    public short BrandId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>A responsible employee of a warehouse (<c>inventory.warehouse_responsible</c>).</summary>
internal sealed class WarehouseResponsible : Entity<long>, IResponsibility
{
    private WarehouseResponsible()
        : base(default) => Role = null!;

    public WarehouseResponsible(short warehouseId, Guid employeeId, string role, Guid? createdBy)
        : base(default)
    {
        WarehouseId = warehouseId;
        EmployeeId = employeeId;
        Role = role;
        CreatedBy = createdBy;
    }

    public short WarehouseId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>A responsible employee of a manufacturer (<c>catalog.manufacturer_responsible</c>, PR 7h4), e.g. its account manager.</summary>
internal sealed class ManufacturerResponsible : Entity<long>, IResponsibility
{
    private ManufacturerResponsible()
        : base(default) => Role = null!;

    public ManufacturerResponsible(short manufacturerId, Guid employeeId, string role, Guid? createdBy)
        : base(default)
    {
        ManufacturerId = manufacturerId;
        EmployeeId = employeeId;
        Role = role;
        CreatedBy = createdBy;
    }

    public short ManufacturerId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>A responsible employee of a part (<c>catalog.part_responsible</c>, PR 7i1b): product specialist or buyer, no rights.</summary>
internal sealed class PartResponsible : Entity<long>, IResponsibility
{
    private PartResponsible()
        : base(default) => Role = null!;

    public PartResponsible(long partId, Guid employeeId, string role, Guid? createdBy)
        : base(default)
    {
        PartId = partId;
        EmployeeId = employeeId;
        Role = role;
        CreatedBy = createdBy;
    }

    public long PartId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public string Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }
}

/// <summary>What replacing the responsibles of a record changes (pure, unit-tested).</summary>
internal sealed record ResponsibilityChange(
    IReadOnlyList<ResponsibleEmployee> Removed,
    IReadOnlyList<ResponsibleEmployee> Added,
    bool RightsChanged,
    IReadOnlyList<ResponsibleEmployee> RightsBearing);

internal static class ResponsibilityRules
{
    /// <summary>
    /// Compares the current and the requested set. <paramref name="grantsScope"/> tells which roles give rights; only a
    /// change among those reaches Identity (<c>ResponsibilitiesChanged</c>), with the full new rights-bearing set.
    /// </summary>
    public static ResponsibilityChange Replace(
        IReadOnlyCollection<ResponsibleEmployee> current, IReadOnlyCollection<ResponsibleEmployee> requested, Func<string, bool> grantsScope)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(grantsScope);
        var target = requested.Distinct().ToList();
        var removed = current.Except(target).ToList();
        var added = target.Except(current).ToList();
        var rightsChanged = removed.Concat(added).Any(r => grantsScope(r.Role));
        var rightsBearing = target.Where(r => grantsScope(r.Role)).OrderBy(r => r.EmployeeId).ThenBy(r => r.Role, StringComparer.Ordinal).ToList();
        return new ResponsibilityChange(removed, added, rightsChanged, rightsBearing);
    }
}
