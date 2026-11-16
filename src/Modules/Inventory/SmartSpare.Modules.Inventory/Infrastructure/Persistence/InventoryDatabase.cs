using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

/// <summary>Migrations of the Inventory module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class InventoryDatabase : ModuleDatabase<InventoryDbContext>
{
    public static InventoryDatabase Instance { get; } = new();

    private InventoryDatabase()
    {
    }

    public override string Name => "inventory";

    public override string HistorySchema => InventoryDbContext.HistorySchema;

    protected override InventoryDbContext CreateContext(DbContextOptions<InventoryDbContext> options) => new(options);

    protected override void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        ArgumentNullException.ThrowIfNull(npgsql);
        npgsql.MapEnum<ReservationStatus>("reservation_status", "inventory");
        npgsql.MapEnum<TransferStatus>("transfer_status", "inventory");
        npgsql.MapEnum<ImportSource>("import_source", "raw");
        npgsql.MapEnum<ImportStatus>("import_status", "raw");
    }
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class InventoryDesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        InventoryDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
