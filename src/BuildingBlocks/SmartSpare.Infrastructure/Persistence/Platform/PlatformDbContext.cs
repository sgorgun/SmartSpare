using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Infrastructure.Persistence.Platform;

/// <summary>
/// DbContext of the Platform (shared kernel) schemas <c>ref</c>, <c>util</c> and <c>audit</c>
/// (ADR 0011, MODULE_MAPPING section 1.1). It is migrated first; module contexts follow.
/// Names, types, constraints and comments mirror V002 (ref), V001 (util.document_counter),
/// V009 (audit.audit_log) and the column documentation of V016, so the EF-built schema matches
/// the SQL-built one. Everything EF cannot model lives in db/sql/platform/.
/// </summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string HistorySchema = "util";

    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();


    public DbSet<MachineType> MachineTypes => Set<MachineType>();

    public DbSet<PartCategory> PartCategories => Set<PartCategory>();

    public DbSet<OperatingCondition> OperatingConditions => Set<OperatingCondition>();

    public DbSet<MovementType> MovementTypes => Set<MovementType>();

    public DbSet<DemandClass> DemandClasses => Set<DemandClass>();

    public DbSet<SeasonPhase> SeasonPhases => Set<SeasonPhase>();

    public DbSet<AgroCalendarWeek> AgroCalendarWeeks => Set<AgroCalendarWeek>();

    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trusted extensions (V001); on Azure they must be allow-listed in azure.extensions.
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.HasPostgresExtension("pgcrypto");

        ConfigureUnitOfMeasure(modelBuilder.Entity<UnitOfMeasure>());
        ConfigureAssignmentRole(modelBuilder.Entity<AssignmentRole>());
        ConfigureMachineType(modelBuilder.Entity<MachineType>());
        ConfigurePartCategory(modelBuilder.Entity<PartCategory>());
        ConfigureOperatingCondition(modelBuilder.Entity<OperatingCondition>());
        ConfigureMovementType(modelBuilder.Entity<MovementType>());
        ConfigureDemandClass(modelBuilder.Entity<DemandClass>());
        ConfigureSeasonPhase(modelBuilder.Entity<SeasonPhase>());
        ConfigureAgroCalendarWeek(modelBuilder.Entity<AgroCalendarWeek>());
        ConfigureDocumentCounter(modelBuilder.Entity<DocumentCounter>());
        ConfigureAuditLog(modelBuilder.Entity<AuditLogEntry>());
    }

    private static void ConfigureUnitOfMeasure(EntityTypeBuilder<UnitOfMeasure> e)
    {
        e.ToTable("unit_of_measure", "ref", t =>
        {
            t.HasComment("Units of measure of spare parts (PCS, L, KG, M, SET).");
            t.HasCheckConstraint("ck_unit_of_measure_code", "code ~ '^[A-Z0-9]{1,10}$'");
        });
        e.HasKey(x => x.Id).HasName("pk_unit_of_measure");
        e.HasAlternateKey(x => x.Code).HasName("uq_unit_of_measure_code");
        e.Property(x => x.Id).UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        e.Property(x => x.Code).HasMaxLength(10).HasComment("Business code (natural key).");
        e.Property(x => x.Name).HasMaxLength(50).HasComment("Display name.");
        e.Property(x => x.IsFractional).HasDefaultValue(false)
            .HasComment("TRUE if quantities may be fractional (litres of oil); FALSE for pieces.");
    }

    private static void ConfigureAssignmentRole(EntityTypeBuilder<AssignmentRole> e)
    {
        e.ToTable("assignment_role", "ref", t =>
        {
            t.HasComment("Roles of responsible employees per record kind (ADR 0026); grants_scope marks the roles that give rights.");
            t.HasCheckConstraint("ck_assignment_role_kind", "record_kind IN ('brand', 'warehouse', 'manufacturer', 'part', 'supplier')");
            t.HasCheckConstraint("ck_assignment_role_code", "code ~ '^[A-Z_]{3,30}$'");
        });
        e.HasKey(x => new { x.RecordKind, x.Code }).HasName("pk_assignment_role");
        e.Property(x => x.RecordKind).HasMaxLength(20).HasComment("Kind of record the role applies to.");
        e.Property(x => x.Code).HasMaxLength(30).HasComment("Business code, e.g. BRAND_MANAGER.");
        e.Property(x => x.Name).HasMaxLength(80).HasComment("Display name.");
        e.Property(x => x.GrantsScope).HasComment("TRUE if the assignment gives a right: brand approval scope or hub access.");
    }

    private static void ConfigureMachineType(EntityTypeBuilder<MachineType> e)
    {
        e.ToTable("machine_type", "ref", t =>
            t.HasComment("Machine types: tractor, combine harvester, seeder, sprayer, forage harvester ..."));
        e.HasKey(x => x.Id).HasName("pk_machine_type");
        e.HasAlternateKey(x => x.Code).HasName("uq_machine_type_code");
        e.Property(x => x.Id).UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        e.Property(x => x.Code).HasMaxLength(20).HasComment("Business code (natural key).");
        e.Property(x => x.Name).HasMaxLength(80).HasComment("Display name.");
    }

    private static void ConfigurePartCategory(EntityTypeBuilder<PartCategory> e)
    {
        e.ToTable("part_category", "ref", t =>
        {
            t.HasComment("Hierarchical (self-referencing) spare part category tree, e.g. FILTERS > OIL_FILTERS.");
            t.HasCheckConstraint("ck_part_category_not_self", "parent_id IS DISTINCT FROM id");
        });
        e.HasKey(x => x.Id).HasName("pk_part_category");
        e.HasAlternateKey(x => x.Code).HasName("uq_part_category_code");
        e.Property(x => x.Id).UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        e.Property(x => x.ParentId)
            .HasComment("Parent category; NULL for a root category (recursive 1:N relationship).");
        e.Property(x => x.Code).HasMaxLength(30).HasComment("Business code (natural key).");
        e.Property(x => x.Name).HasMaxLength(120).HasComment("Display name.");
        e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId)
            .HasConstraintName("fk_part_category_parent").OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.ParentId).HasDatabaseName("ix_part_category_parent");
    }

    private static void ConfigureOperatingCondition(EntityTypeBuilder<OperatingCondition> e)
    {
        e.ToTable("operating_condition", "ref", t =>
        {
            t.HasComment("Operating conditions that shorten OEM service intervals (finding F5: e.g. non-prescribed oil halves the interval).");
            t.HasCheckConstraint("ck_operating_condition_factor", "interval_factor > 0 AND interval_factor <= 1");
        });
        e.HasKey(x => x.Id).HasName("pk_operating_condition");
        e.HasAlternateKey(x => x.Code).HasName("uq_operating_condition_code");
        e.Property(x => x.Id).UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        e.Property(x => x.Code).HasMaxLength(20).HasComment("Business code (natural key).");
        e.Property(x => x.Name).HasMaxLength(120).HasComment("Display name.");
        e.Property(x => x.IntervalFactor).HasPrecision(4, 2)
            .HasComment("Multiplier applied to the OEM interval: 1.00 = normal, 0.50 = halved.");
    }

    private static void ConfigureMovementType(EntityTypeBuilder<MovementType> e)
    {
        e.ToTable("movement_type", "ref", t =>
        {
            t.HasComment("Types of stock ledger movements and their effect on the on-hand balance.");
            t.HasCheckConstraint("ck_movement_type_code", "code ~ '^[A-Z_]{3,20}$'");
            t.HasCheckConstraint("ck_movement_type_sign", "balance_sign IN (-1, 0, 1)");
            t.HasCheckConstraint("ck_movement_type_demand_is_issue", "NOT is_demand OR balance_sign IN (-1, 0)");
        });
        e.HasKey(x => x.Code).HasName("pk_movement_type");
        e.Property(x => x.Code).HasMaxLength(20).HasComment("Surrogate primary key.");
        e.Property(x => x.Name).HasMaxLength(80).HasComment("Display name.");
        e.Property(x => x.BalanceSign)
            .HasComment("+1 increases on-hand stock, -1 decreases it, 0 = no physical effect (imported historical demand).");
        e.Property(x => x.IsDemand)
            .HasComment("TRUE if the movement represents customer/service demand used for forecasting.");
        e.Property(x => x.Description).HasComment("Description.");
    }

    private static void ConfigureDemandClass(EntityTypeBuilder<DemandClass> e)
    {
        e.ToTable("demand_class", "ref", t =>
        {
            t.HasComment("Syntetos-Boylan demand classification by ADI (cut-off 1.32) and CV² (cut-off 0.49); ranges are [lower, upper).");
            t.HasCheckConstraint("ck_demand_class_adi", "adi_lower >= 0 AND adi_upper > adi_lower");
            t.HasCheckConstraint("ck_demand_class_cv2", "cv2_lower >= 0 AND cv2_upper > cv2_lower");
        });
        e.HasKey(x => x.Code).HasName("pk_demand_class");
        e.Property(x => x.Code).HasMaxLength(15).HasComment("Surrogate primary key.");
        e.Property(x => x.Name).HasMaxLength(40).HasComment("Display name.");
        e.Property(x => x.AdiLower).HasPrecision(6, 3);
        e.Property(x => x.AdiUpper).HasPrecision(6, 3);
        e.Property(x => x.Cv2Lower).HasColumnName("cv2_lower").HasPrecision(6, 3);
        e.Property(x => x.Cv2Upper).HasColumnName("cv2_upper").HasPrecision(6, 3);
        e.Property(x => x.Description).HasComment("Description.");
    }

    private static void ConfigureSeasonPhase(EntityTypeBuilder<SeasonPhase> e)
    {
        e.ToTable("season_phase", "ref", t =>
            t.HasComment("Agrotechnical season phases (winter repair, sowing, crop care, harvest, autumn tillage)."));
        e.HasKey(x => x.Code).HasName("pk_season_phase");
        e.Property(x => x.Code).HasMaxLength(20).HasComment("Surrogate primary key.");
        e.Property(x => x.Name).HasMaxLength(80).HasComment("Display name.");
    }

    private static void ConfigureAgroCalendarWeek(EntityTypeBuilder<AgroCalendarWeek> e)
    {
        e.ToTable("agro_calendar_week", "ref", t =>
        {
            t.HasComment("ISO week -> season phase mapping for Lithuania; ML feature (EP03, finding F8).");
            t.HasCheckConstraint("ck_agro_calendar_week_range", "iso_week BETWEEN 1 AND 53");
            t.HasCheckConstraint("ck_agro_calendar_week_weight", "seasonal_weight > 0 AND seasonal_weight <= 5");
        });
        e.HasKey(x => x.IsoWeek).HasName("pk_agro_calendar_week");
        e.Property(x => x.IsoWeek).ValueGeneratedNever().HasComment("Surrogate primary key.");
        e.Property(x => x.SeasonPhaseCode).HasMaxLength(20).HasComment("Reference to ref.season_phase.");
        e.Property(x => x.SeasonalWeight).HasPrecision(4, 2)
            .HasComment("Expert seasonal intensity (1.00 = average week); peaks Feb-Mar and Jul-Sep.");
        e.HasOne(x => x.SeasonPhase).WithMany().HasForeignKey(x => x.SeasonPhaseCode)
            .HasConstraintName("fk_agro_calendar_week_phase").OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => x.SeasonPhaseCode).HasDatabaseName("ix_agro_calendar_week_phase");
    }

    private static void ConfigureDocumentCounter(EntityTypeBuilder<DocumentCounter> e)
    {
        // V001 declared the CHECKs inline without names; PostgreSQL named them <table>_<column>_check.
        e.ToTable("document_counter", "util", t =>
        {
            t.HasComment("Last issued business document number per prefix and year (gap-less numbering).");
            t.HasCheckConstraint("document_counter_prefix_check", "prefix ~ '^[A-Z]{2,4}$'");
            t.HasCheckConstraint("document_counter_year_check", "year BETWEEN 2000 AND 2100");
            t.HasCheckConstraint("document_counter_last_value_check", "last_value >= 0");
        });
        e.HasKey(x => new { x.Prefix, x.Year }).HasName("pk_document_counter");
        e.Property(x => x.Prefix).HasMaxLength(4);
        e.Property(x => x.Year).ValueGeneratedNever();
        e.Property(x => x.LastValue).HasDefaultValueSql("0");
    }

    private static void ConfigureAuditLog(EntityTypeBuilder<AuditLogEntry> e)
    {
        // Partitioned table: created with its partitions, indexes and triggers by
        // db/sql/platform/0002_audit_log.sql, never by EF (ADR 0004).
        e.ToTable("audit_log", "audit", t => t.ExcludeFromMigrations());
        e.HasKey(x => new { x.Id, x.OccurredAt }).HasName("pk_audit_log");
        e.Property(x => x.Id).UseIdentityByDefaultColumn();
        e.Property(x => x.OccurredAt).HasDefaultValueSql("clock_timestamp()");
        e.Property(x => x.DbRole).HasColumnType("name").HasDefaultValueSql("SESSION_USER");
        e.Property(x => x.Action).HasMaxLength(10);
        e.Property(x => x.TableName).HasMaxLength(80);
        e.Property(x => x.RecordId).HasMaxLength(80);
        e.Property(x => x.OldValues).HasColumnType("jsonb");
        e.Property(x => x.NewValues).HasColumnType("jsonb");
        e.Property(x => x.ChangedColumns).HasColumnType("text[]");
        e.Property(x => x.Reason).HasMaxLength(300);
        e.Property(x => x.TransactionId).HasDefaultValueSql("txid_current()");
    }
}
