using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary><c>catalog.part_analogue</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class PartAnalogueConfiguration : IEntityTypeConfiguration<PartAnalogue>
{
    public void Configure(EntityTypeBuilder<PartAnalogue> builder)
    {
        builder.ToTable("part_analogue", "catalog", t =>
        {
            t.HasComment("New SKU -> historical analogue/predecessor SKU with a demand scaling factor (US15).");
            t.HasCheckConstraint("ck_part_analogue_distinct", "(part_id <> analogue_part_id)");
            t.HasCheckConstraint("ck_part_analogue_factor", "((scaling_factor > (0)::numeric) AND (scaling_factor <= (10)::numeric))");
        });

        builder.HasKey(x => new { x.PartId, x.AnaloguePartId }).HasName("pk_part_analogue");

        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(0).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.AnaloguePartId).HasColumnName("analogue_part_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to catalog.part.");
        builder.Property(x => x.ScalingFactor).HasColumnName("scaling_factor").HasColumnOrder(2).HasColumnType("numeric(5,3)").HasDefaultValueSql("1.000").HasComment("Multiplier applied to the analogue's demand history (US15).");
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnOrder(3).HasColumnType("date").HasDefaultValueSql("CURRENT_DATE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(4).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");

        builder.HasOne<Part>().WithMany()
            .HasForeignKey(x => x.AnaloguePartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_analogue_analogue").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Part>().WithMany()
            .HasForeignKey(x => x.PartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_analogue_part").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.AnaloguePartId).HasDatabaseName("ix_part_analogue_analogue");
    }
}
