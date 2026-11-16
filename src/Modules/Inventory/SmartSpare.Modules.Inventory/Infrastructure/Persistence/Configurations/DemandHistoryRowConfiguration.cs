using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>raw.demand_history_row</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class DemandHistoryRowConfiguration : IEntityTypeConfiguration<DemandHistoryRow>
{
    public void Configure(EntityTypeBuilder<DemandHistoryRow> builder)
    {
        builder.ToTable("demand_history_row", "raw", t =>
        {
            t.HasComment("Raw layer: historical demand (ERP sales/service issues) CSV rows as text.");
            t.HasCheckConstraint("ck_demand_history_row_line", "(line_no > 0)");
        });

        builder.HasKey(x => new { x.ImportBatchId, x.LineNo }).HasName("pk_demand_history_row");

        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to raw.import_batch.");
        builder.Property(x => x.LineNo).HasColumnName("line_no").HasColumnOrder(1).HasColumnType("integer").HasComment("Line number in the uploaded file.");
        builder.Property(x => x.DocDate).HasColumnName("doc_date").HasColumnOrder(2).HasColumnType("text");
        builder.Property(x => x.WarehouseCode).HasColumnName("warehouse_code").HasColumnOrder(3).HasColumnType("text");
        builder.Property(x => x.BrandCode).HasColumnName("brand_code").HasColumnOrder(4).HasColumnType("text");
        builder.Property(x => x.OemPartNumber).HasColumnName("oem_part_number").HasColumnOrder(5).HasColumnType("text");
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasColumnOrder(6).HasColumnType("text").HasComment("Quantity in the unit of measure of the part.");
        builder.Property(x => x.DocRef).HasColumnName("doc_ref").HasColumnOrder(7).HasColumnType("text");

        builder.HasOne<ImportBatch>().WithMany()
            .HasForeignKey(x => x.ImportBatchId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_demand_history_row_batch").OnDelete(DeleteBehavior.Cascade);
    }
}
