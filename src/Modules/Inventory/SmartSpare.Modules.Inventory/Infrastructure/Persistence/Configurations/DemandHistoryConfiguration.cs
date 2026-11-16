using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>staging.demand_history</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class DemandHistoryConfiguration : IEntityTypeConfiguration<DemandHistory>
{
    public void Configure(EntityTypeBuilder<DemandHistory> builder)
    {
        builder.ToTable("demand_history", "staging", t =>
        {
            t.HasComment("Staging layer: typed historical demand rows; duplicates (same row_hash as an earlier loaded row) are flagged, not loaded.");
            t.HasCheckConstraint("ck_staging_demand_history_valid", "((NOT is_valid) OR ((occurred_at IS NOT NULL) AND (warehouse_id IS NOT NULL) AND (part_id IS NOT NULL) AND (quantity > (0)::numeric) AND (row_hash IS NOT NULL)))");
        });

        builder.HasKey(x => new { x.ImportBatchId, x.LineNo }).HasName("pk_staging_demand_history");

        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to raw.demand_history_row.");
        builder.Property(x => x.LineNo).HasColumnName("line_no").HasColumnOrder(1).HasColumnType("integer").HasComment("Reference to raw.demand_history_row.");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnOrder(2).HasColumnType("timestamp with time zone").HasComment("Time of the occurred event.");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(3).HasColumnType("smallint");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(4).HasColumnType("bigint");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(5).HasColumnType("numeric(12,3)").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.DocRef).HasColumnName("doc_ref").HasColumnOrder(6).HasColumnType("character varying(40)");
        builder.Property(x => x.RowHash).HasColumnName("row_hash").HasColumnOrder(7).HasColumnType("bytea").HasComment("SHA-256 of the normalised business key (date|hub|part|doc_ref|qty) for deduplication.");
        builder.Property(x => x.IsValid).HasColumnName("is_valid").HasColumnOrder(8).HasColumnType("boolean").HasComment("Flag: valid.");
        builder.Property(x => x.IsDuplicate).HasColumnName("is_duplicate").HasColumnOrder(9).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("TRUE if the row was already loaded by this or an earlier batch.");

        builder.HasOne<DemandHistoryRow>().WithMany()
            .HasForeignKey(x => new { x.ImportBatchId, x.LineNo })
            .HasPrincipalKey(x => new { x.ImportBatchId, x.LineNo })
            .HasConstraintName("fk_staging_demand_history_raw").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.RowHash).HasDatabaseName("ix_staging_demand_history_hash").HasFilter("(is_valid AND (NOT is_duplicate))");
    }
}
