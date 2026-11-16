using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using SmartSpare.Infrastructure.Messaging.Outbox;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// DbContext of the Identity module: schema(s) iam (users, roles, brand assignments) (ADR 0011, MODULE_MAPPING section 1.2).
/// Tables, keys, intra-module FKs, indexes, CHECKs, enums and comments are modelled here with the names
/// of V016; functions, triggers, partitions, grants and seed data live in db/sql/identity/.
/// </summary>
internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string HistorySchema = "iam";

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
