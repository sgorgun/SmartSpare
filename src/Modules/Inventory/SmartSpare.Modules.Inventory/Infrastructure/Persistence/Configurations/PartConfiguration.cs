using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>catalog.part</c> (V016) as the <see cref="Part"/> catalogue record (PR 7i1): the code is <c>oem_part_number</c>
/// (unique per brand), the name is <c>description</c>, and the archive columns of the shared mechanism replace
/// <c>is_active</c> (one meaning, STAGE_7_PLAN 2.1).
/// </summary>
internal sealed class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public const string Table = "part";

    public void Configure(EntityTypeBuilder<Part> builder)
    {
        builder.ToTable(Table, "catalog", t =>
        {
            t.HasComment("Spare part (SKU) catalogue; natural key = brand + OEM part number.");
            t.HasCheckConstraint("ck_part_description", "(length(btrim((description)::text)) >= 3)");
            t.HasCheckConstraint("ck_part_oem_number", "((oem_part_number)::text ~ '^[A-Z0-9][A-Z0-9.\\-/]{2,39}$'::text)");
            t.HasCheckConstraint("ck_part_unit_cost", "(unit_cost >= (0)::numeric)");
            t.HasCheckConstraint("ck_part_weight", "((weight_kg IS NULL) OR (weight_kg >= (0)::numeric))");
            t.HasArchiveCheck(Table);
        });

        builder.HasKey(x => x.Id).HasName("pk_part");
        builder.HasAlternateKey(x => new { x.BrandId, x.Code }).HasName("uq_part_brand_oem");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to catalog.brand.");
        builder.Property(x => x.Code).HasColumnName("oem_part_number").HasColumnOrder(2).HasColumnType("character varying(40)").HasComment("Manufacturer part number, upper-case, normalised during import.");
        builder.Property(x => x.Name).HasColumnName("description").HasColumnOrder(3).HasColumnType("character varying(300)").HasComment("Description.");
        builder.Property(x => x.CategoryId).HasColumnName("category_id").HasColumnOrder(4).HasColumnType("integer").HasComment("Reference to ref.part_category.");
        builder.Property(x => x.UomId).HasColumnName("uom_id").HasColumnOrder(5).HasColumnType("smallint").HasComment("Reference to ref.unit_of_measure.");
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasColumnOrder(6).HasColumnType("numeric(9,3)");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnOrder(7).HasColumnType("numeric(12,2)").HasComment("Standard (valuation) cost per unit, EUR. Money is numeric, never float.");
        builder.Property(x => x.IsConditionBased).HasColumnName("is_condition_based").HasColumnOrder(8).HasColumnType("boolean").HasDefaultValueSql("false").HasComment("TRUE for parts replaced \"as required\" (air/cab filters, finding F6): demand only from the forecast stream.").ValueGeneratedNever();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(10).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.").ValueGeneratedOnAdd();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(11).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time of the last change (maintained by trigger).").ValueGeneratedOnAdd();
        builder.Property(x => x.ManufacturerId).HasColumnName("manufacturer_id").HasColumnOrder(12).HasColumnType("smallint")
            .HasComment("Maker of the part (catalog.manufacturer), if known.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(13).HasColumnType("boolean")
            .HasDefaultValueSql("false").ValueGeneratedNever().HasComment("Archived instead of deleted: kept on history, cannot be chosen (SS500).");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnOrder(14).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ArchivedBy).HasColumnName("archived_by").HasColumnOrder(15).HasColumnType("uuid")
            .HasComment("User who archived the record (iam.app_user).");
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne(x => x.Manufacturer).WithMany().HasForeignKey(x => x.ManufacturerId).HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_manufacturer").OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(x => x.ManufacturerId).HasDatabaseName("ix_part_manufacturer");
        builder.HasOne(x => x.Brand).WithMany()
            .HasForeignKey(x => x.BrandId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_brand").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Category).WithMany()
            .HasForeignKey(x => x.CategoryId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_category").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.Unit).WithMany()
            .HasForeignKey(x => x.UomId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_part_uom").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.CategoryId).HasDatabaseName("ix_part_category");
        builder.HasIndex(x => x.Name).HasDatabaseName("ix_part_description_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.HasIndex(x => x.Code).HasDatabaseName("ix_part_oem_number_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.HasIndex(x => x.UomId).HasDatabaseName("ix_part_uom");
        builder.Ignore(x => x.DomainEvents);
    }
}
