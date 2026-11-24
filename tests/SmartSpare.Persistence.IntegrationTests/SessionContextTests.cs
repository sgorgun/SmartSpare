using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>
/// The session context of #11 (STAGE_3_PLAN section 6, MODULE_MAPPING 3.6): ClaimsCurrentUser reads the request's claims,
/// SessionContextInterceptor writes app.user_id, app.user_roles and app.home_warehouse_id transaction-locally, and the
/// database checks read them. The connection uses the API login, as the host does.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class SessionContextTests(MigratedDatabaseFixture database)
{
    private static readonly Guid UserId = Guid.NewGuid();

    public static TheoryData<string> AuthenticationTypes => ["SmartSpare.Cookie", InternalTokenDefaults.AuthenticationType];

    [Theory]
    [MemberData(nameof(AuthenticationTypes))]
    public async Task The_claims_of_the_request_become_the_session_context(string authenticationType)
    {
        await using var context = CreateContext(Request(authenticationType, [AppRoles.WarehouseKeeper, AppRoles.Sponsor], warehouse: 3));
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        var session = await SessionAsync(context);
        session.UserId.ShouldBe(UserId.ToString());
        session.Roles.ShouldBe($"{AppRoles.Sponsor},{AppRoles.WarehouseKeeper}");
        session.Warehouse.ShouldBe("3");
        session.WarehouseFunction.ShouldBe("3");
        (await RequireSponsorAsync(context)).ShouldBe(UserId.ToString());
    }

    [Fact]
    public async Task A_role_missing_from_the_claims_fails_the_database_check()
    {
        await using var context = CreateContext(Request("SmartSpare.Cookie", [AppRoles.ServiceEngineer], warehouse: null));
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        var error = await Should.ThrowAsync<PostgresException>(() => RequireSponsorAsync(context));
        error.SqlState.ShouldBe("SS301");
    }

    [Fact]
    public async Task The_values_are_transaction_local_and_empty_without_a_user()
    {
        await using (var context = CreateContext(Request("SmartSpare.Cookie", [AppRoles.Administrator], warehouse: 1)))
        {
            await context.Database.OpenConnectionAsync(Ct);
            await using (await context.Database.BeginTransactionAsync(Ct))
            {
                (await SessionAsync(context)).Roles.ShouldBe(AppRoles.Administrator);
            }

            // Same connection, after the rollback: nothing is left for the next user of a pooled connection.
            var after = await SessionAsync(context);
            after.Roles.ShouldBeNullOrEmpty();
            after.UserId.ShouldBeNullOrEmpty();
        }

        await using var anonymous = CreateContext(new DefaultHttpContext());
        await using var transaction = await anonymous.Database.BeginTransactionAsync(Ct);
        var none = await SessionAsync(anonymous);
        none.Roles.ShouldBeEmpty();
        none.WarehouseFunction.ShouldBeNull();
        var error = await Should.ThrowAsync<PostgresException>(() => RequireSponsorAsync(anonymous));
        error.SqlState.ShouldBe("SS301");
    }

    [Fact]
    public async Task An_acting_user_during_sign_in_has_no_roles()
    {
        var acting = new ActingUser();
        await using var context = CreateContext(Request("SmartSpare.Cookie", [AppRoles.Administrator], warehouse: 1), acting);
        var signingIn = Guid.NewGuid();
        using var scope = acting.ActAs(signingIn, "sign-in");
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        var session = await SessionAsync(context);
        session.UserId.ShouldBe(signingIn.ToString());
        session.Reason.ShouldBe("sign-in");
        session.Roles.ShouldBeEmpty();
    }

    private DbContext CreateContext(HttpContext request, ActingUser? acting = null)
    {
        var currentUser = new ClaimsCurrentUser(new HttpContextAccessor { HttpContext = request });
        var options = new DbContextOptionsBuilder()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(database.AdminConnectionString)
            {
                Username = "smartspare_api",
                Password = database.Options.Passwords.Api,
            }.ConnectionString)
            .AddInterceptors(new SessionContextInterceptor(currentUser, acting ?? new ActingUser()))
            .Options;
        return new DbContext(options);
    }

    /// <summary>A request authenticated by <paramref name="authenticationType"/>: cookie and internal token carry the same claim types.</summary>
    private static DefaultHttpContext Request(string authenticationType, string[] roles, short? warehouse)
    {
        var claims = new List<Claim> { new(AppClaimTypes.Subject, UserId.ToString()), new(AppClaimTypes.Name, "session.user") };
        claims.AddRange(roles.Select(r => new Claim(AppClaimTypes.Role, r)));
        if (warehouse is { } w)
        {
            claims.Add(new Claim(AppClaimTypes.Warehouse, w.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, AppClaimTypes.Name, AppClaimTypes.Role)),
        };
    }

    private static Task<Session> SessionAsync(DbContext context) =>
        context.Database.SqlQuery<Session>($"""
            SELECT current_setting('app.user_id', true)           AS "UserId",
                   current_setting('app.reason', true)            AS "Reason",
                   current_setting('app.user_roles', true)        AS "Roles",
                   current_setting('app.home_warehouse_id', true) AS "Warehouse",
                   util.current_home_warehouse_id()::text         AS "WarehouseFunction"
            """).SingleAsync(Ct);

    private static Task<string> RequireSponsorAsync(DbContext context) =>
        context.Database.SqlQuery<string>($"SELECT util.require_role('SPONSOR', 'ADMINISTRATOR')::text AS \"Value\"").SingleAsync(Ct);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Session(string? UserId, string? Reason, string? Roles, string? Warehouse, string? WarehouseFunction);
}
