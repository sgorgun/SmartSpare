using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// DbContext of the Identity module: schema(s) iam (users, roles, brand assignments) (ADR 0011, MODULE_MAPPING section 1.2).
/// Tables, keys, intra-module FKs, indexes, CHECKs, enums and comments are modelled here with the names
/// of V016; functions, triggers, partitions, grants and seed data live in db/sql/identity/.
/// </summary>
internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public const string HistorySchema = "iam";

    /// <summary>
    /// The ASP.NET Core Data Protection key ring (<c>iam.data_protection_keys</c>, #22): technical state, not a domain
    /// aggregate, so it stays a framework class (ADR 0013 rule 3). Used only if the host persists keys here.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every index is declared explicitly with its V016 name; EF must not add FK indexes of its own.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);

        // Transactional outbox and idempotent inbox of this module (ADR 0003, MODULE_MAPPING 5.6).
        modelBuilder.AddOutboxAndInbox(HistorySchema);
    }
}
