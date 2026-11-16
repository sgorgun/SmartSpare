using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>staging.import_error</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class ImportErrorConfiguration : IEntityTypeConfiguration<ImportError>
{
    public void Configure(EntityTypeBuilder<ImportError> builder)
    {
        builder.ToTable("import_error", "staging", t =>
        {
            t.HasComment("Data-quality errors found while validating raw rows (shown to the user after an upload).");
            // Source text of V005/V010: the catalogue form of IN (...) on varchar does not round-trip.
            t.HasCheckConstraint("ck_import_error_code", "error_code IN ('REQUIRED', 'TYPE', 'RANGE', 'FORMAT', 'UNKNOWN_REF', 'DUPLICATE')");
        });

        builder.HasKey(x => x.Id).HasName("pk_import_error");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ImportBatchId).HasColumnName("import_batch_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to raw.import_batch.");
        builder.Property(x => x.LineNo).HasColumnName("line_no").HasColumnOrder(2).HasColumnType("integer").HasComment("Line number in the uploaded file.");
        builder.Property(x => x.ColumnName).HasColumnName("column_name").HasColumnOrder(3).HasColumnType("character varying(40)");
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasColumnOrder(4).HasColumnType("character varying(30)");
        builder.Property(x => x.Message).HasColumnName("message").HasColumnOrder(5).HasColumnType("character varying(300)");

        builder.HasOne<ImportBatch>().WithMany()
            .HasForeignKey(x => x.ImportBatchId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_import_error_batch").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ImportBatchId, x.LineNo }).HasDatabaseName("ix_import_error_batch");
    }
}
