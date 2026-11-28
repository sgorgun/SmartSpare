using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>catalog.part_analogue</c> as typed links (ADR 0027, PR 7i2a): a surrogate key, the link type and the audit columns.
/// The pair index on least/greatest, the cycle trigger and the views are SQL (inventory/0023_part_analogues.sql).
/// </summary>
internal sealed class PartAnalogueConfiguration : IEntityTypeConfiguration<PartAnalogue>
{
    public const string Table = "part_analogue";

    public void Configure(EntityTypeBuilder<PartAnalogue> builder)
    {
        builder.ToTable(Table, "catalog", t =>
        {
            t.HasComment("Typed link between two parts (ADR 0027): interchangeable (stored once, smaller id first) or supersedes (new part -> old part), with a demand scaling factor (US15).");
            t.HasCheckConstraint("ck_part_analogue_distinct", "(part_id <> analogue_part_id)");
            t.HasCheckConstraint("ck_part_analogue_factor", "((scaling_factor > (0)::numeric) AND (scaling_factor <= (10)::numeric))");
            t.HasCheckConstraint("ck_part_analogue_interchangeable_order", "((link_type <> 'interchangeable'::catalog.analogue_link_type) OR (part_id < analogue_part_id))");
        });

        builder.HasKey(x => x.Id).HasName("pk_part_analogue");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(0).HasColumnType("bigint")
            .HasComment("The part; for a supersession the new part, for an interchangeable link the smaller id.");
        builder.Property(x => x.AnaloguePartId).HasColumnName("analogue_part_id").HasColumnOrder(1).HasColumnType("bigint")
            .HasComment("The analogue; for a supersession the old part it replaces.");
        builder.Property(x => x.ScalingFactor).HasColumnName("scaling_factor").HasColumnOrder(2).HasColumnType("numeric(5,3)").HasDefaultValueSql("1.000")
            .HasComment("Units of the analogue per unit of the part, applied to the analogue's demand history (US15).");
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnOrder(3).HasColumnType("date").HasDefaultValueSql("CURRENT_DATE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(4).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()")
            .HasComment("Time the record was created.").ValueGeneratedOnAdd();
        builder.Property(x => x.LinkType).HasColumnName("link_type").HasColumnOrder(5).HasColumnType("catalog.analogue_link_type")
            .HasComment("interchangeable (symmetric, groups) or supersedes (new -> old, at most one successor, no cycles).").ValueGeneratedNever();
        builder.Property(x => x.Note).HasColumnName("note").HasColumnOrder(6).HasColumnType("character varying(300)");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnOrder(7).HasColumnType("uuid").HasComment("User who linked the parts.");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(8).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()")
            .HasComment("Time of the last change (maintained by trigger).").ValueGeneratedOnAdd();
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<Part>().WithMany()
            .HasForeignKey(x => x.AnaloguePartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_analogue_analogue").OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Part>().WithMany()
            .HasForeignKey(x => x.PartId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_analogue_part").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.AnaloguePartId, "ix_part_analogue_analogue").HasDatabaseName("ix_part_analogue_analogue");
        builder.HasIndex(x => x.PartId).HasDatabaseName("ix_part_analogue_part");
        builder.HasIndex(x => x.AnaloguePartId, "uq_part_analogue_one_successor").HasDatabaseName("uq_part_analogue_one_successor").IsUnique()
            .HasFilter("(link_type = 'supersedes'::catalog.analogue_link_type)");
        builder.Ignore(x => x.DomainEvents);
    }
}
