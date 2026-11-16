using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>forecast.recommendation</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class RecommendationConfiguration : IEntityTypeConfiguration<Recommendation>
{
    public void Configure(EntityTypeBuilder<Recommendation> builder)
    {
        builder.ToTable("recommendation", "forecast", t =>
        {
            t.HasComment("Explainable transfer / purchase proposal. Stores every driver of the calculation (NFR08) and the human decision (KPI-04).");
            t.HasCheckConstraint("ck_recommendation_accepted", "((status <> 'accepted'::forecast.recommendation_status) OR (final_qty = recommended_qty))");
            t.HasCheckConstraint("ck_recommendation_cost", "((estimated_cost_eur >= (0)::numeric) AND ((alternative_cost_eur IS NULL) OR (alternative_cost_eur >= (0)::numeric)))");
            t.HasCheckConstraint("ck_recommendation_decided", "((status = ANY (ARRAY['open'::forecast.recommendation_status, 'expired'::forecast.recommendation_status])) = (decided_at IS NULL))");
            t.HasCheckConstraint("ck_recommendation_drivers", "((base_demand_qty >= (0)::numeric) AND (maintenance_demand_qty >= (0)::numeric) AND (lead_time_buffer_qty >= (0)::numeric) AND (safety_stock_qty >= (0)::numeric) AND (available_qty >= (0)::numeric) AND (in_transit_qty >= (0)::numeric))");
            t.HasCheckConstraint("ck_recommendation_final_qty", "((status <> ALL (ARRAY['accepted'::forecast.recommendation_status, 'overridden'::forecast.recommendation_status])) OR ((final_qty > (0)::numeric) AND (decided_by IS NOT NULL)))");
            t.HasCheckConstraint("ck_recommendation_kind_purchase", "((kind <> 'purchase'::forecast.recommendation_kind) OR ((supplier_id IS NOT NULL) AND (donor_warehouse_id IS NULL)))");
            t.HasCheckConstraint("ck_recommendation_kind_transfer", "((kind <> 'transfer'::forecast.recommendation_kind) OR ((donor_warehouse_id IS NOT NULL) AND (supplier_id IS NULL) AND (donor_warehouse_id <> warehouse_id)))");
            t.HasCheckConstraint("ck_recommendation_net_position", "(net_position_qty = ((available_qty + in_transit_qty) - (((base_demand_qty + maintenance_demand_qty) + lead_time_buffer_qty) + safety_stock_qty)))");
            t.HasCheckConstraint("ck_recommendation_override", "((status <> ALL (ARRAY['overridden'::forecast.recommendation_status, 'rejected'::forecast.recommendation_status])) OR (decision_reason IS NOT NULL))");
            t.HasCheckConstraint("ck_recommendation_override_qty", "((status <> 'overridden'::forecast.recommendation_status) OR (final_qty <> recommended_qty))");
            t.HasCheckConstraint("ck_recommendation_qty", "(recommended_qty > (0)::numeric)");
        });

        builder.HasKey(x => x.Id).HasName("pk_recommendation");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("bigint").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.ForecastRunId).HasColumnName("forecast_run_id").HasColumnOrder(1).HasColumnType("bigint").HasComment("Reference to forecast.forecast_run.");
        builder.Property(x => x.Kind).HasColumnName("kind").HasColumnOrder(2);
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").HasColumnOrder(3).HasColumnType("smallint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.PartId).HasColumnName("part_id").HasColumnOrder(4).HasColumnType("bigint").HasComment("Reference to inventory.warehouse_part.");
        builder.Property(x => x.DonorWarehouseId).HasColumnName("donor_warehouse_id").HasColumnOrder(5).HasColumnType("smallint").HasComment("Reference to inventory.warehouse.");
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id").HasColumnOrder(6).HasColumnType("integer").HasComment("Reference to procurement.supplier.");
        builder.Property(x => x.BaseDemandQty).HasColumnName("base_demand_qty").HasColumnOrder(7).HasColumnType("numeric(12,3)").HasComment("NFR08 (a): ML forecast over the horizon.");
        builder.Property(x => x.MaintenanceDemandQty).HasColumnName("maintenance_demand_qty").HasColumnOrder(8).HasColumnType("numeric(12,3)").HasComment("NFR08 (b): planned maintenance demand not yet reserved.");
        builder.Property(x => x.LeadTimeBufferQty).HasColumnName("lead_time_buffer_qty").HasColumnOrder(9).HasColumnType("numeric(12,3)").HasComment("NFR08 (c): demand expected during the replenishment lead time.");
        builder.Property(x => x.SafetyStockQty).HasColumnName("safety_stock_qty").HasColumnOrder(10).HasColumnType("numeric(12,3)").HasComment("NFR08 (d): safety stock of the hub.");
        builder.Property(x => x.AvailableQty).HasColumnName("available_qty").HasColumnOrder(11).HasColumnType("numeric(12,3)");
        builder.Property(x => x.InTransitQty).HasColumnName("in_transit_qty").HasColumnOrder(12).HasColumnType("numeric(12,3)");
        builder.Property(x => x.NetPositionQty).HasColumnName("net_position_qty").HasColumnOrder(13).HasColumnType("numeric(12,3)").HasComment("Available + in transit - (a + b + c + d); negative = shortage.");
        builder.Property(x => x.RecommendedQty).HasColumnName("recommended_qty").HasColumnOrder(14).HasColumnType("numeric(12,3)");
        builder.Property(x => x.EstimatedCostEur).HasColumnName("estimated_cost_eur").HasColumnOrder(15).HasColumnType("numeric(12,2)");
        builder.Property(x => x.AlternativeCostEur).HasColumnName("alternative_cost_eur").HasColumnOrder(16).HasColumnType("numeric(12,2)").HasComment("Cost of the alternative (e.g. emergency supplier order EUR 280 vs. transfer EUR 45, UC01 step 9).");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(17).HasDefaultValueSql("'open'::forecast.recommendation_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.FinalQty).HasColumnName("final_qty").HasColumnOrder(18).HasColumnType("numeric(12,3)").HasComment("Quantity decided by the user (KPI-04: accepted when equal to recommended_qty).");
        builder.Property(x => x.DecidedBy).HasColumnName("decided_by").HasColumnOrder(19).HasColumnType("uuid").HasComment("User who performed the step (iam.app_user).");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at").HasColumnOrder(20).HasColumnType("timestamp with time zone").HasComment("Time of the decided event.");
        builder.Property(x => x.DecisionReason).HasColumnName("decision_reason").HasColumnOrder(21).HasColumnType("character varying(300)");
        builder.Property(x => x.TransferOrderId).HasColumnName("transfer_order_id").HasColumnOrder(22).HasColumnType("bigint").HasComment("Reference to inventory.transfer_order.");
        builder.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id").HasColumnOrder(23).HasColumnType("bigint").HasComment("Reference to procurement.purchase_order.");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(24).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");
        // fk_recommendation_decided_by -> iam.app_user removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_recommendation_donor -> inventory.warehouse removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_recommendation_po -> procurement.purchase_order removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_recommendation_supplier -> procurement.supplier removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_recommendation_transfer -> inventory.transfer_order removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).
        // fk_recommendation_wh_part -> inventory.warehouse_part removed: cross-module FK (ADR 0011, MODULE_MAPPING section 2).

        builder.HasOne<ForecastRun>().WithMany()
            .HasForeignKey(x => x.ForecastRunId)
            .HasPrincipalKey(x => x.Id)
            .HasConstraintName("fk_recommendation_run").OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.DecidedBy).HasDatabaseName("ix_recommendation_decided_by");
        builder.HasIndex(x => x.DonorWarehouseId).HasDatabaseName("ix_recommendation_donor").HasFilter("(donor_warehouse_id IS NOT NULL)");
        builder.HasIndex(x => new { x.Kind, x.CreatedAt }).HasDatabaseName("ix_recommendation_open").HasFilter("(status = 'open'::forecast.recommendation_status)");
        builder.HasIndex(x => x.PurchaseOrderId).HasDatabaseName("ix_recommendation_po").HasFilter("(purchase_order_id IS NOT NULL)");
        builder.HasIndex(x => x.ForecastRunId).HasDatabaseName("ix_recommendation_run");
        builder.HasIndex(x => x.SupplierId).HasDatabaseName("ix_recommendation_supplier").HasFilter("(supplier_id IS NOT NULL)");
        builder.HasIndex(x => x.TransferOrderId).HasDatabaseName("ix_recommendation_transfer").HasFilter("(transfer_order_id IS NOT NULL)");
        builder.HasIndex(x => new { x.WarehouseId, x.PartId }).HasDatabaseName("ix_recommendation_wh_part");
    }
}
