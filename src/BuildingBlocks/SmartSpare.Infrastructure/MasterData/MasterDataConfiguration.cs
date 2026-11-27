using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// The Fluent mapping every catalogue shares (ADR 0025, STAGE_7_PLAN 2.1): surrogate key, unique code, name, the
/// archive columns with their consistency check, timestamps, <c>xmin</c> as the concurrency token and trigram indexes
/// for search. The table's audit and <c>updated_at</c> triggers and its grants are SQL in the module's migration.
/// </summary>
public static class MasterDataConfiguration
{
    /// <summary>Maps the columns of <see cref="MasterRecord{TId}"/>; the catalogue maps its own columns from order 3 on.</summary>
    public static EntityTypeBuilder<TRecord> MapMasterRecord<TRecord, TId>(
        this EntityTypeBuilder<TRecord> builder, string table, int codeLength, string codeExample)
        where TRecord : MasterRecord<TId>
        where TId : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => x.Id).HasName($"pk_{table}");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).UseIdentityByDefaultColumn().HasComment("Surrogate primary key.");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnOrder(1).HasColumnType($"character varying({codeLength})")
            .HasComment($"Business key, e.g. {codeExample}; never changes after creation.");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnOrder(2)
            .HasColumnType($"character varying({MasterRecordRules.MaxNameLength})").HasComment("Display name.");
        builder.Property(x => x.IsArchived).HasColumnName("is_archived").HasColumnOrder(50).HasColumnType("boolean")
            .HasDefaultValueSql("false").ValueGeneratedNever().HasComment("Archived instead of deleted: kept on history, cannot be chosen (SS500).");
        builder.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnOrder(51).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ArchivedBy).HasColumnName("archived_by").HasColumnOrder(52).HasColumnType("uuid")
            .HasComment("User who archived the record (iam.app_user).");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnOrder(53).HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnOrder(54).HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(x => x.Code, $"uq_{table}_code").IsUnique().HasDatabaseName($"uq_{table}_code");
        builder.HasIndex(x => x.Code, $"ix_{table}_code_trgm").HasDatabaseName($"ix_{table}_code_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.HasIndex(x => x.Name, $"ix_{table}_name_trgm").HasDatabaseName($"ix_{table}_name_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        builder.Ignore(x => x.DomainEvents);
        return builder;
    }

    /// <summary>The CHECK that keeps <c>is_archived</c> and <c>archived_at</c> consistent.</summary>
    public static void HasArchiveCheck<TRecord>(this TableBuilder<TRecord> table, string tableName)
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(table);
        table.HasCheckConstraint($"ck_{tableName}_archived", "((is_archived AND (archived_at IS NOT NULL)) OR ((NOT is_archived) AND (archived_at IS NULL)))");
    }
}
