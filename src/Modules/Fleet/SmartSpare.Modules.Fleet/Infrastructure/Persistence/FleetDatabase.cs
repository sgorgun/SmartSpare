using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

/// <summary>Migrations of the Fleet module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class FleetDatabase : ModuleDatabase<FleetDbContext>
{
    public static FleetDatabase Instance { get; } = new();

    private FleetDatabase()
    {
    }

    public override string Name => "fleet";

    public override string HistorySchema => FleetDbContext.HistorySchema;

    protected override FleetDbContext CreateContext(DbContextOptions<FleetDbContext> options) => new(options);

    protected override void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        npgsql.MapEnum<MachineStatus>("machine_status", "fleet");
        npgsql.MapEnum<ReadingSource>("reading_source", "fleet");
        npgsql.MapEnum<ServiceOrderStatus>("service_order_status", "fleet");
    }
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class FleetDesignTimeFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args) =>
        FleetDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
