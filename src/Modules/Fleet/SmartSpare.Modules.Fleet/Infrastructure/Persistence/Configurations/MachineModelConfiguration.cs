using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Fleet.Infrastructure.Persistence.Configurations;

/// <summary><c>fleet.machine_model</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class MachineModelConfiguration : IEntityTypeConfiguration<MachineModel>
{
    public void Configure(EntityTypeBuilder<MachineModel> builder)
    {
        builder.ToTable("machine_model", "fleet", t =>
        {
            t.HasComment("Machine model, e.g. Claas Lexion 8700, John Deere 6R 150.");
        });

        builder.HasKey(x => x.Id).HasName("pk_machine_model");
        builder.HasAlternateKey(x => new { x.BrandId, x.ModelName }).HasName("uq_machine_model_brand_name");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("integer").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.BrandId).HasColumnName("brand_id").HasColumnOrder(1).HasColumnType("smallint").HasComment("Reference to ref.brand.");
        builder.Property(x => x.MachineTypeId).HasColumnName("machine_type_id").HasColumnOrder(2).HasColumnType("smallint").HasComment("Reference to ref.machine_type.");
        builder.Property(x => x.ModelName).HasColumnName("model_name").HasColumnOrder(3).HasColumnType("character varying(80)");

        builder.HasOne<RefBrand>().WithMany()
            .HasForeignKey(x => x.BrandId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_machine_model_brand").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<RefMachineType>().WithMany()
            .HasForeignKey(x => x.MachineTypeId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_machine_model_type").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.MachineTypeId).HasDatabaseName("ix_machine_model_type");
    }
}
