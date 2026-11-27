using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

/// <summary>The claims of a session (STAGE_3_PLAN 3.1), built by the module's own registration over PostgreSQL.</summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class ClaimsPrincipalFactoryTests(IdentityDatabaseFixture database)
{
    [Fact]
    public async Task The_claims_are_id_name_stamp_roles_brands_and_warehouse_without_personal_data()
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT INTO iam.employee (id, code, full_name, position_id)
            SELECT '{id}', 'E9001', 'Claims Person', id FROM iam.position WHERE code = 'COMMERCIAL_DIRECTOR';
            INSERT INTO iam.app_user (id, employee_id, user_name, email, full_name, phone, password_hash, home_warehouse_id)
            VALUES ('{id}', '{id}', 'claims.user', 'claims.user@dealer.example', 'Claims Person', '+370 600 00000',
                    'AQAAAAIAA1tgAAAAE{new string('A', 67)}', 3);
            INSERT INTO iam.user_role (user_id, role_id) SELECT '{id}', id FROM iam.role WHERE code IN ('BRAND_MANAGER', 'SPONSOR');
            INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
            SELECT 'brand', id, '{id}'::uuid, 'BRAND_MANAGER' FROM ref.brand WHERE code IN ('CLAAS', 'LEMKEN')
            UNION ALL SELECT 'warehouse', 3, '{id}'::uuid, 'KEEPER';
            """);

        var services = new ServiceCollection().AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:smartspare"] = database.MigratorConnectionString })
            .Build();
        new IdentityModuleInstaller().AddServices(services, configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>();

        var user = await users.FindByNameAsync("Claims.User");
        user.ShouldNotBeNull("lookup is case-insensitive");
        var principal = await factory.CreateAsync(user);

        var claims = principal.Claims.Select(c => (c.Type, c.Value)).ToList();
        claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal).ShouldBe(
            new[] { AppClaimTypes.Subject, AppClaimTypes.Name, AppClaimTypes.Role, AppClaimTypes.Brand, AppClaimTypes.Warehouse, IdentityClaimTypes.SecurityStamp }
                .Order(StringComparer.Ordinal));
        claims.ShouldContain((AppClaimTypes.Subject, id.ToString()));
        claims.ShouldContain((AppClaimTypes.Name, "claims.user"));
        claims.Where(c => c.Type == AppClaimTypes.Role).Select(c => c.Value).ShouldBe([AppRoles.BrandManager, AppRoles.Sponsor], ignoreOrder: true);
        claims.Count(c => c.Type == AppClaimTypes.Brand).ShouldBe(2);
        claims.ShouldContain((AppClaimTypes.Warehouse, 3.ToString(CultureInfo.InvariantCulture)));
        claims.ShouldContain((IdentityClaimTypes.SecurityStamp, user.SecurityStamp.ToString("N")));
        claims.ShouldNotContain(c => c.Value.Contains("dealer.example", StringComparison.Ordinal)
                                     || c.Value.Contains("Claims Person", StringComparison.Ordinal)
                                     || c.Value.Contains("+370", StringComparison.Ordinal));
    }
}
