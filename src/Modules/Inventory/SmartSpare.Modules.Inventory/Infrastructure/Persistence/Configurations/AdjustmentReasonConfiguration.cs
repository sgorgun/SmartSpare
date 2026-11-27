using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Stock;

namespace SmartSpare.Modules.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>inventory.adjustment_reason</c>: why stock is corrected (STAGE_7_PLAN 3.1, ADR 0025). Triggers, grants, the seed and
/// the reference from <c>stock_movement</c>: <c>db/sql/inventory/0014_adjustment_reasons.sql</c>.
/// </summary>
internal sealed class AdjustmentReasonConfiguration : IEntityTypeConfiguration<AdjustmentReason>
{
    public const string Table = "adjustment_reason";

    public void Configure(EntityTypeBuilder<AdjustmentReason> builder)
    {
        builder.ToTable(Table, "inventory", t =>
        {
            t.HasComment("Why stock is corrected; every stock adjustment names an active reason (ADR 0025).");
            t.HasCheckConstraint("ck_adjustment_reason_code", "((code)::text ~ '^[A-Z0-9_]{2,30}$'::text)");
            t.HasCheckConstraint("ck_adjustment_reason_direction", "((direction)::text = ANY (ARRAY['IN'::text, 'OUT'::text, 'BOTH'::text]))");
            t.HasArchiveCheck(Table);
        });

        builder.MapMasterRecord<AdjustmentReason, short>(Table, 30, "DAMAGED");
        builder.Property(x => x.Id).HasColumnType("smallint");
        builder.Property(x => x.Direction).HasColumnName("direction").HasColumnOrder(3).HasColumnType("character varying(4)")
            .HasComment("IN: surpluses only; OUT: shortages only; BOTH.");
        builder.Property(x => x.RequiresNote).HasColumnName("requires_note").HasColumnOrder(4).HasColumnType("boolean")
            .HasDefaultValueSql("false").ValueGeneratedNever().HasComment("An adjustment with this reason must have a note.");
    }
}
