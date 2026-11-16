using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.warehouse_route</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class WarehouseRouteConfiguration : IEntityTypeConfiguration<WarehouseRoute>
{
    public void Configure(EntityTypeBuilder<WarehouseRoute> builder)
    {
        builder.ToTable("warehouse_route", "inventory", t =>
        {
            t.HasComment("Fixed inter-depot transport matrix (M:N warehouse-warehouse): distance, transit time, cost. Used by the rebalancing engine (US10).");
            t.HasCheckConstraint("ck_warehouse_route_cost", "(transport_cost_eur >= (0)::numeric)");
            t.HasCheckConstraint("ck_warehouse_route_distance", "(distance_km > (0)::numeric)");
            t.HasCheckConstraint("ck_warehouse_route_distinct", "(from_warehouse_id <> to_warehouse_id)");
            t.HasCheckConstraint("ck_warehouse_route_transit", "((transit_hours > (0)::numeric) AND (transit_hours <= (240)::numeric))");
        });

        builder.HasKey(x => new { x.FromWarehouseId, x.ToWarehouseId }).HasName("pk_warehouse_route");

        builder.Property(x => x.FromWarehouseId).HasColumnName("from_warehouse_id").HasColumnOrder(0).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.ToWarehouseId).HasColumnName("to_warehouse_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.DistanceKm).HasColumnName("distance_km").HasColumnOrder(2).HasColumnType("numeric(6,1)");
        builder.Property(x => x.TransitHours).HasColumnName("transit_hours").HasColumnOrder(3).HasColumnType("numeric(5,1)").HasComment("Planned inter-depot transport time incl. consolidation.");
        builder.Property(x => x.TransportCostEur).HasColumnName("transport_cost_eur").HasColumnOrder(4).HasColumnType("numeric(10,2)").HasComment("Cost of one inter-depot shipment (EUR).");

        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(x => x.FromWarehouseId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_warehouse_route_from").OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Warehouse>().WithMany()
            .HasForeignKey(x => x.ToWarehouseId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_warehouse_route_to").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ToWarehouseId).HasDatabaseName("ix_warehouse_route_to");
    }
}
