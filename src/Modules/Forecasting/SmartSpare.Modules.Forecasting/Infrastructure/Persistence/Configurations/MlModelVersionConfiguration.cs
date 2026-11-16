using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Forecasting.Infrastructure.Persistence.Configurations;

/// <summary><c>forecast.ml_model_version</c> as created by V016 (names, types, constraints and comments unchanged).</summary>
internal sealed class MlModelVersionConfiguration : IEntityTypeConfiguration<MlModelVersion>
{
    public void Configure(EntityTypeBuilder<MlModelVersion> builder)
    {
        builder.ToTable("ml_model_version", "forecast", t =>
        {
            t.HasComment("Versioned ML / benchmark models. A candidate is promoted to active only if it beats the best benchmark (forecast.promote_model).");
            t.HasCheckConstraint("ck_ml_model_version_active", "((status <> 'active'::forecast.model_status) OR (promoted_at IS NOT NULL))");
            t.HasCheckConstraint("ck_ml_model_version_artifact", "((algorithm <> 'fasttree'::forecast.forecast_method) OR (status = 'training'::forecast.model_status) OR (artifact_uri IS NOT NULL))");
            t.HasCheckConstraint("ck_ml_model_version_chronological_holdout", "((holdout_from > training_to) AND (holdout_to >= holdout_from))");
            t.HasCheckConstraint("ck_ml_model_version_evaluated", "((status = ANY (ARRAY['training'::forecast.model_status, 'rejected'::forecast.model_status])) OR ((rmsse IS NOT NULL) AND (trained_at IS NOT NULL)))");
            t.HasCheckConstraint("ck_ml_model_version_hyperparameters", "(jsonb_typeof(hyperparameters) = 'object'::text)");
            t.HasCheckConstraint("ck_ml_model_version_label", "((version_label)::text ~ '^v\\d+\\.\\d+(\\.\\d+)?$'::text)");
            t.HasCheckConstraint("ck_ml_model_version_metrics", "(((rmsse IS NULL) OR (rmsse >= (0)::numeric)) AND ((mase IS NULL) OR (mase >= (0)::numeric)) AND ((mae IS NULL) OR (mae >= (0)::numeric)) AND ((rmse IS NULL) OR (rmse >= (0)::numeric)))");
            t.HasCheckConstraint("ck_ml_model_version_sha", "((artifact_sha256 IS NULL) OR (artifact_sha256 ~ '^[0-9a-f]{64}$'::text))");
            t.HasCheckConstraint("ck_ml_model_version_training", "(training_to > training_from)");
        });

        builder.HasKey(x => x.Id).HasName("pk_ml_model_version");
        builder.HasAlternateKey(x => new { x.Algorithm, x.VersionLabel }).HasName("uq_ml_model_version_label");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).HasColumnType("integer").UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Algorithm).HasColumnName("algorithm").HasColumnOrder(1);
        builder.Property(x => x.VersionLabel).HasColumnName("version_label").HasColumnOrder(2).HasColumnType("character varying(30)");
        builder.Property(x => x.DemandClassCode).HasColumnName("demand_class_code").HasColumnOrder(3).HasColumnType("character varying(15)").HasComment("Reference to ref.demand_class.");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnOrder(4).HasDefaultValueSql("'training'::forecast.model_status").HasComment("Workflow status (state machine guarded by trigger).");
        builder.Property(x => x.TrainingFrom).HasColumnName("training_from").HasColumnOrder(5).HasColumnType("date");
        builder.Property(x => x.TrainingTo).HasColumnName("training_to").HasColumnOrder(6).HasColumnType("date");
        builder.Property(x => x.HoldoutFrom).HasColumnName("holdout_from").HasColumnOrder(7).HasColumnType("date");
        builder.Property(x => x.HoldoutTo).HasColumnName("holdout_to").HasColumnOrder(8).HasColumnType("date");
        builder.Property(x => x.Rmsse).HasColumnName("rmsse").HasColumnOrder(9).HasColumnType("numeric(8,4)").HasComment("Root mean squared scaled error on the holdout period (KPI-02 metric).");
        builder.Property(x => x.Mase).HasColumnName("mase").HasColumnOrder(10).HasColumnType("numeric(8,4)").HasComment("Mean absolute scaled error on the holdout period.");
        builder.Property(x => x.Mae).HasColumnName("mae").HasColumnOrder(11).HasColumnType("numeric(12,4)");
        builder.Property(x => x.Rmse).HasColumnName("rmse").HasColumnOrder(12).HasColumnType("numeric(12,4)");
        builder.Property(x => x.Hyperparameters).HasColumnName("hyperparameters").HasColumnOrder(13).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").HasComment("Algorithm-specific settings as a JSON object, e.g. {\"numberOfTrees\":200,\"numberOfLeaves\":31,\"learningRate\":0.1}.");
        builder.Property(x => x.ArtifactUri).HasColumnName("artifact_uri").HasColumnOrder(14).HasColumnType("character varying(500)").HasComment("Location of the serialised ML.NET model (.zip) in Azure Blob Storage.");
        builder.Property(x => x.ArtifactSha256).HasColumnName("artifact_sha256").HasColumnOrder(15).HasColumnType("character(64)");
        builder.Property(x => x.TrainedAt).HasColumnName("trained_at").HasColumnOrder(16).HasColumnType("timestamp with time zone").HasComment("Time of the trained event.");
        builder.Property(x => x.PromotedAt).HasColumnName("promoted_at").HasColumnOrder(17).HasColumnType("timestamp with time zone").HasComment("Time of the promoted event.");
        builder.Property(x => x.RetiredAt).HasColumnName("retired_at").HasColumnOrder(18).HasColumnType("timestamp with time zone").HasComment("Time of the retired event.");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(19).HasColumnType("timestamp with time zone").HasDefaultValueSql("now()").HasComment("Time the record was created.");

        builder.HasOne<RefDemandClass>().WithMany()
            .HasForeignKey(x => x.DemandClassCode)
            .HasPrincipalKey(x => x.Code)
            .HasConstraintName("fk_ml_model_version_class").OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => x.DemandClassCode).HasDatabaseName("ix_ml_model_version_class");
        // uq_ml_model_version_one_active is an expression index (CREATE UNIQUE INDEX uq_ml_model_version_one_active ON forecast.ml_model_version USING btree (COALESCE(demand_class_code, '*'::character varying)) WHERE (status = 'active'::forecast.model_status)); created in SQL.
    }
}
