using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>inventory.demand_exception</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class DemandExceptionConfiguration : IEntityTypeConfiguration<DemandException>
{
    public void Configure(EntityTypeBuilder<DemandException> builder)
    {
        builder.ToTable("demand_exception", "inventory", t =>
        {
            t.HasComment("1:1 annotation of a demand movement as exceptional (US08). Kept separate because the ledger is immutable; excluded from ML training.");
            // Source text of V005/V010: the catalogue form of IN (...) on varchar does not round-trip.
            t.HasCheckConstraint("ck_demand_exception_method", "detection_method IN ('manual', 'auto_3sigma')");
            t.HasCheckConstraint("ck_demand_exception_reason", "(length(btrim((reason)::text)) >= 5)");
        });

        builder.HasKey(x => new { x.MovementId, x.MovementOccurredAt }).HasName("pk_demand_exception");

        builder.Property(x => x.MovementId).HasColumnName("movement_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to inventory.stock_movement.");
        builder.Property(x => x.MovementOccurredAt).HasColumnName("movement_occurred_at").HasColumnOrder(1).HasColumnType("timestamp with time zone").HasComment("Reference to inventory.stock_movement.");
        builder.Property(x => x.Reason).HasColumnName("reason").HasColumnOrder(2).HasColumnType("character varying(300)");
        builder.Property(x => x.DetectionMethod).HasColumnName("detection_method").HasColumnOrder(3).HasColumnType("character varying(20)").HasDefaultValueSql("'manual'::character varying");
        builder.Property(x => x.FlaggedBy).HasColumnName("flagged_by").HasColumnOrder(4).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.FlaggedAt).HasColumnName("flagged_at").HasColumnOrder(5).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the flagged event.");
        // fk_demand_exception_flagged_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<StockMovement>().WithMany()
            .HasForeignKey(x => new { x.MovementId, x.MovementOccurredAt })
            .HasPrincipalKey(x => new { x.Id, x.OccurredAt })
            .HasConstraintName("fk_demand_exception_movement").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.FlaggedBy).HasDatabaseName("ix_demand_exception_flagged_by");
    }
}
