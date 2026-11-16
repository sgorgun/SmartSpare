using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>raw.import_batch</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("import_batch", "raw", t =>
        {
            t.HasComment("One uploaded CSV file: source, SHA-256 (duplicate-file protection), processing status and row counts (load monitoring).");
            t.HasCheckConstraint("ck_import_batch_counts", "((total_rows >= 0) AND ((COALESCE(valid_rows, 0) + COALESCE(error_rows, 0)) <= total_rows) AND (COALESCE(loaded_rows, 0) <= COALESCE(valid_rows, total_rows)))");
            t.HasCheckConstraint("ck_import_batch_processed", "((status = 'uploaded'::raw.import_status) OR (processed_at IS NOT NULL))");
            t.HasCheckConstraint("ck_import_batch_sha", "(file_sha256 ~ '^[0-9a-f]{64}$'::text)");
        });

        builder.HasKey(x => x.Id).HasName("pk_import_batch");
        builder.HasAlternateKey(x => new { x.Source, x.FileSha256 }).HasName("uq_import_batch_file");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Source).HasColumnName("source").HasColumnOrder(1);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasColumnOrder(2).HasColumnType("character varying(255)");
        builder.Property(x => x.FileSha256).HasColumnName("file_sha256").HasColumnOrder(3).HasColumnType("character(64)").HasComment("SHA-256 of the file content; the same file cannot be imported twice.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'uploaded'::raw.import_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.TotalRows).HasColumnName("total_rows").HasColumnOrder(5).HasColumnType("integer").HasDefaultValueSql("0");
        builder.Property(x => x.ValidRows).HasColumnName("valid_rows").HasColumnOrder(6).HasColumnType("integer");
        builder.Property(x => x.ErrorRows).HasColumnName("error_rows").HasColumnOrder(7).HasColumnType("integer");
        builder.Property(x => x.LoadedRows).HasColumnName("loaded_rows").HasColumnOrder(8).HasColumnType("integer");
        builder.Property(x => x.UploadedBy).HasColumnName("uploaded_by").HasColumnOrder(9).HasColumnType("uuid").HasComment("User who created the record (iam.app_user).");
        builder.Property(x => x.UploadedAt).HasColumnName("uploaded_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the uploaded event.");
        builder.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasComment("Time of the processed event.");
        builder.Property(x => x.Message).HasColumnName("message").HasColumnOrder(12).HasColumnType("text");
        // fk_import_batch_user -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasIndex(x => new { x.Status, x.UploadedAt }).HasDatabaseName("ix_import_batch_status").IsDescending(false, true);
        builder.HasIndex(x => x.UploadedBy).HasDatabaseName("ix_import_batch_user");
    }
}
