using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence;

/// <summary>
/// DbContext of the Inventory module: schema(s) catalog (parts), inventory, raw and staging (ADR 0011, MODULE_MAPPING section 1.3).
/// Tables, keys, intra-module FKs, indexes, CHECKs, enums and comments are modelled here with the names
/// of V016; functions, triggers, partitions, grants and seed data live in db/sql/inventory/.
/// </summary>
internal sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public const string HistorySchema = "inventory";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every index is declared explicitly with its V016 name; EF must not add FK indexes of its own.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<ReservationStatus>("inventory", "reservation_status");
        modelBuilder.HasPostgresEnum<TransferStatus>("inventory", "transfer_status");
        modelBuilder.HasPostgresEnum<ImportSource>("raw", "import_source");
        modelBuilder.HasPostgresEnum<ImportStatus>("raw", "import_status");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);

        // Transactional outbox and idempotent inbox of this module (ADR 0003, MODULE_MAPPING 5.6).
        modelBuilder.AddOutboxAndInbox(HistorySchema);
    }
}
