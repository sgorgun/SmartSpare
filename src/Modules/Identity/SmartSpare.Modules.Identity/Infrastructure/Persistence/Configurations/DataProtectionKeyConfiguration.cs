using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>iam.data_protection_keys</c>: the Data Protection key ring shared by every instance of the single host, so a
/// cookie issued by one instance is accepted by another (STAGE_3_PLAN section 5, #22). The key XML is secret material:
/// only <c>app_write</c> may read and insert it, <c>app_read</c> gets nothing, and the audit trigger is not attached
/// (db/sql/identity/0005_data_protection_keys.sql).
/// </summary>
internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys", "iam", t =>
            t.HasComment("ASP.NET Core Data Protection key ring of the single host (cookies, antiforgery tokens). Secret material: no app_read grant, no audit."));

        builder.HasKey(x => x.Id).HasName("pk_data_protection_keys");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnOrder(0).UseIdentityByDefaultColumn();
        builder.Property(x => x.FriendlyName).HasColumnName("friendly_name").HasColumnOrder(1).HasColumnType("text")
            .HasComment("Key id as written by the key manager (key-<guid>).");
        builder.Property(x => x.Xml).HasColumnName("xml").HasColumnOrder(2).HasColumnType("text")
            .HasComment("Key descriptor XML; encrypted at rest when the host configures key protection.");
    }
}
