using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Procurement.Infrastructure.Persistence;

/// <summary>Migrations of the Procurement module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class ProcurementDatabase : ModuleDatabase<ProcurementDbContext>
{
    public static ProcurementDatabase Instance { get; } = new();

    private ProcurementDatabase()
    {
    }

    public override string Name => "procurement";

    public override string HistorySchema => ProcurementDbContext.HistorySchema;

    protected override ProcurementDbContext CreateContext(DbContextOptions<ProcurementDbContext> options) => new(options);

    protected override void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        npgsql.MapEnum<PoStatus>("po_status", "procurement");
    }
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class ProcurementDesignTimeFactory : IDesignTimeDbContextFactory<ProcurementDbContext>
{
    public ProcurementDbContext CreateDbContext(string[] args) =>
        ProcurementDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
