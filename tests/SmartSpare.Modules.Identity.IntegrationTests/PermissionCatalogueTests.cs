using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Queries;

namespace SmartSpare.Modules.Identity;

/// <summary>
/// The database mirrors the code (ADR 0028): <c>iam.permission</c> holds exactly the catalogue of <see cref="Permissions"/>,
/// and the seeded roles hold exactly the default bundles of <see cref="DefaultRoles"/>. A permission added in code without
/// a migration, or a seed that drifts from the mapping table, fails here.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class PermissionCatalogueTests(IdentityDatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_permission_table_is_the_code_catalogue()
    {
        await using var db = database.CreateContext();

        var rows = await db.Set<PermissionDefinition>().AsNoTracking().OrderBy(p => p.Code).Select(p => new { p.Code, p.Module, p.Description }).ToListAsync(Ct);

        rows.Select(r => (r.Code, r.Module, r.Description)).ShouldBe(
            Permissions.All.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => (p.Name, p.Module, p.Description)));
    }

    [Fact]
    public async Task The_seeded_roles_hold_the_default_bundles()
    {
        await using var db = database.CreateContext();

        var bundles = await new RolePermissionReader(db).GetAsync(Ct);

        bundles.Keys.Order(StringComparer.Ordinal).ShouldBe(AppRoles.All.Order(StringComparer.Ordinal));
        foreach (var role in AppRoles.All)
        {
            bundles[role].ShouldBe([.. DefaultRoles.Bundles[role].Order(StringComparer.Ordinal)], role);
        }
    }

    [Fact]
    public async Task A_role_permission_must_exist_in_the_catalogue()
    {
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => database.ExecuteAsync(
            "INSERT INTO iam.role_permission (role_id, permission) SELECT id, 'Inventory.Part.Delete' FROM iam.role WHERE code = 'SPONSOR'"));

        error.SqlState.ShouldBe("23503", "fk_role_permission_permission: no permission outside the code catalogue");
    }
}
