using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>
/// The transaction decorator, the unit of work and the outbox on real PostgreSQL (MODULE_MAPPING 5.6):
/// a rolled-back command leaves neither data nor an outbox row, a committed one leaves both, and the
/// dispatcher delivers each event once.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratedDatabaseGroup.Name)]
public sealed class UnitOfWorkTests(MigratedDatabaseFixture db) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var assembly = typeof(UnitOfWorkTests).Assembly;
        services.AddHandlersFromAssembly(assembly);
        // A probe "module": its context maps iam.role and the iam outbox/inbox of the Identity migrations.
        services.AddModulePersistence<ProbeDbContext>(assembly, db.AdminConnectionString, "iam", outboxSchema: "iam");
        services.DecorateHandlers();
        services.AddOutboxDispatcher();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(ProbeOutcome.Failure)]
    [InlineData(ProbeOutcome.Exception)]
    public async Task RolledBackCommand_LeavesNeitherDataNorOutboxRow(ProbeOutcome outcome)
    {
        var code = "PROBE_" + outcome.ToString().ToUpperInvariant();

        if (outcome == ProbeOutcome.Exception)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(new CreateProbeRole(code, outcome)));
        }
        else
        {
            (await SendAsync(new CreateProbeRole(code, outcome))).IsFailure.ShouldBeTrue();
        }

        (await db.ScalarAsync<long>($"SELECT count(*) FROM iam.role WHERE code = '{code}'")).ShouldBe(0);
        (await db.ScalarAsync<long>($"SELECT count(*) FROM iam.outbox_message WHERE payload ->> 'code' = '{code}'")).ShouldBe(0);
    }

    [Fact]
    public async Task CommittedCommand_LeavesBoth_AndTheDispatcherDeliversTheEventOnce()
    {
        const string code = "PROBE_SUCCESS";
        try
        {
            (await SendAsync(new CreateProbeRole(code, ProbeOutcome.Success))).IsSuccess.ShouldBeTrue();

            (await db.ScalarAsync<long>($"SELECT count(*) FROM iam.role WHERE code = '{code}'")).ShouldBe(1);
            (await db.ScalarAsync<long>(
                $"SELECT count(*) FROM iam.outbox_message WHERE payload ->> 'code' = '{code}' AND processed_at IS NULL")).ShouldBe(1);

            ProbeRoleCreatedConsumer.Received.Clear();
            var dispatcher = _provider.GetServices<IHostedService>().OfType<OutboxDispatcher>().Single();
            (await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
            (await dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

            ProbeRoleCreatedConsumer.Received.ShouldBe([code]);
            (await db.ScalarAsync<long>(
                $"SELECT count(*) FROM iam.outbox_message WHERE payload ->> 'code' = '{code}' AND processed_at IS NOT NULL")).ShouldBe(1);
            (await db.ScalarAsync<long>($"""
                SELECT count(*) FROM iam.inbox_message i JOIN iam.outbox_message o ON o.id = i.message_id
                WHERE o.payload ->> 'code' = '{code}' AND i.consumer = '{typeof(ProbeRoleCreatedConsumer).FullName}'
                """)).ShouldBe(1);
        }
        finally
        {
            // Other tests of the collection count the seeded roles.
            await db.ScalarAsync<int>($"""
                WITH o AS (DELETE FROM iam.outbox_message WHERE payload ->> 'code' = '{code}' RETURNING id),
                     i AS (DELETE FROM iam.inbox_message WHERE message_id IN (SELECT id FROM o) RETURNING 1),
                     r AS (DELETE FROM iam.role WHERE code = '{code}' RETURNING 1)
                SELECT 0
                """);
        }
    }

    private async Task<Result> SendAsync(CreateProbeRole command)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateProbeRole>>()
            .Handle(command, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

public enum ProbeOutcome
{
    Success,
    Failure,
    Exception,
}

internal sealed record CreateProbeRole(string Code, ProbeOutcome Outcome) : ICommand;

internal sealed record ProbeRoleCreated(string Code) : IntegrationEvent;

/// <summary>Writes data and an event, then succeeds, fails or throws.</summary>
internal sealed class CreateProbeRoleHandler(ProbeDbContext context, IUnitOfWork<ProbeDbContext> unitOfWork) : ICommandHandler<CreateProbeRole>
{
    public async Task<Result> Handle(CreateProbeRole command, CancellationToken cancellationToken)
    {
        context.Roles.Add(new ProbeRole { Code = command.Code, Name = "Probe role" });
        unitOfWork.AddIntegrationEvent(new ProbeRoleCreated(command.Code));
        await unitOfWork.SaveChangesAsync(cancellationToken);   // the rows exist inside the transaction

        return command.Outcome switch
        {
            ProbeOutcome.Failure => Result.Failure(Error.Conflict("Probe.Failed", "The probe command failed on purpose.")),
            ProbeOutcome.Exception => throw new InvalidOperationException("The probe command threw on purpose."),
            _ => Result.Success(),
        };
    }
}

internal sealed class ProbeRoleCreatedConsumer : IIntegrationEventConsumer<ProbeRoleCreated>
{
    public static List<string> Received { get; } = [];

    public Task Handle(ProbeRoleCreated integrationEvent, CancellationToken cancellationToken)
    {
        Received.Add(integrationEvent.Code);
        return Task.CompletedTask;
    }
}

internal sealed class ProbeRole
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;
}

internal sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
{
    public DbSet<ProbeRole> Roles => Set<ProbeRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProbeRole>(e =>
        {
            e.ToTable("role", "iam");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
        });
        modelBuilder.AddOutboxAndInbox("iam");
    }
}
