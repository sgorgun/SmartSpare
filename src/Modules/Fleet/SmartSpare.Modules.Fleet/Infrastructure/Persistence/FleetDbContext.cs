using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence;

/// <summary>
/// DbContext of the Fleet module: schema(s) fleet (customers, machines, engine hours, maintenance, service orders) (ADR 0011, MODULE_MAPPING section 1.4).
/// Tables, keys, intra-module FKs, indexes, CHECKs, enums and comments are modelled here with the names
/// of V016; functions, triggers, partitions, grants and seed data live in db/sql/fleet/.
/// </summary>
internal sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public const string HistorySchema = "fleet";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every index is declared explicitly with its V016 name; EF must not add FK indexes of its own.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<MachineStatus>("fleet", "machine_status");
        modelBuilder.HasPostgresEnum<ReadingSource>("fleet", "reading_source");
        modelBuilder.HasPostgresEnum<ServiceOrderStatus>("fleet", "service_order_status");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FleetDbContext).Assembly);
    }
}
