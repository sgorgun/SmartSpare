using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Features.AdjustmentReasons;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The shared catalogue service and history reader (ADR 0025) on PostgreSQL 17: the archive guard's refusal (SS501)
/// and the history read through <c>audit.record_history</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(InventoryDatabaseGroup.Name)]
public sealed class CatalogMechanismTests(InventoryDatabaseFixture database)
{
    private static readonly Guid User = Guid.Parse("00000000-0000-4000-8000-0000000000aa");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_guard_that_finds_uses_refuses_to_archive_with_the_reasons()
    {
        await using var db = database.CreateContext();
        var guarded = new CatalogDefinitionWithGuard(["on hand 12 at KLP", "3 open transfers"]);
        var service = Service(db, guarded.Definition);
        var created = await service.CreateAsync(new AdjustmentReasonWrite("GUARD_" + Suffix(), "Guarded", AdjustmentDirections.Out, false), Ct);

        var result = await service.ArchiveAsync(created.Value.Id, uint.Parse(created.Value.Version, System.Globalization.CultureInfo.InvariantCulture), Ct);

        var blocked = result.Error.ShouldBeOfType<ArchiveBlockedError>();
        blocked.Code.ShouldBe("SS501");
        blocked.Reasons.ShouldBe(["on hand 12 at KLP", "3 open transfers"]);
        (await db.Set<AdjustmentReason>().AsNoTracking().SingleAsync(r => r.Id == created.Value.Id, Ct)).IsArchived.ShouldBeFalse();
    }

    [Fact]
    public async Task The_history_reads_each_change_of_one_record()
    {
        await using var db = database.CreateContext();
        var service = Service(db, AdjustmentReasonCatalog.Definition);
        var created = (await service.CreateAsync(new AdjustmentReasonWrite("HIST_" + Suffix(), "Before", AdjustmentDirections.In, false), Ct)).Value;
        var version = uint.Parse(created.Version, System.Globalization.CultureInfo.InvariantCulture);
        (await service.UpdateAsync(created.Id, new AdjustmentReasonWrite(created.Code, "After", AdjustmentDirections.In, true), version, Ct)).IsSuccess.ShouldBeTrue();

        var page = await RecordHistory.ReadAsync(db, new NoNames(), "inventory.adjustment_reason", created.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            1, 10, AdjustmentReasonCatalog.Definition.FieldLabels, Ct);

        page.TotalCount.ShouldBe(2);
        page.Items[0].Action.ShouldBe("changed");
        page.Items[0].Changes.ShouldBe([new("Name", "Before", "After"), new("Note required", "false", "true")], ignoreOrder: true);
        page.Items[1].Action.ShouldBe("created");
        page.Items.ShouldAllBe(e => e.Actor == null, "the migrator connection has no application user");
    }

    [Fact]
    public async Task A_table_that_is_not_on_the_allow_list_has_no_history()
    {
        await using var db = database.CreateContext();

        await Should.ThrowAsync<Npgsql.PostgresException>(() =>
            RecordHistory.ReadAsync(db, new NoNames(), "iam.app_user", "1", 1, 10, new Dictionary<string, string>(), Ct));
    }

    private static CatalogService<InventoryDbContext, AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite> Service(
        InventoryDbContext db, CatalogDefinition<AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite> definition) =>
        new(db, new EfUnitOfWork<InventoryDbContext>(db), new FixedUser(User), TimeProvider.System, null!, definition);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private sealed class NoNames : IActorNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FullNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private sealed record FixedUser(Guid? UserId) : ICurrentUser
    {
        public string? Reason => null;

        public IReadOnlyCollection<string> Roles => [];

        public IReadOnlyCollection<short> WarehouseIds => [];
    }

    /// <summary>The adjustment reasons catalogue with a guard that always finds uses (the real one has none).</summary>
    private sealed class CatalogDefinitionWithGuard(IReadOnlyList<string> reasons)
    {
        public CatalogDefinition<AdjustmentReason, short, AdjustmentReasonDto, AdjustmentReasonWrite> Definition { get; } =
            AdjustmentReasonCatalog.Definition with { ArchiveGuard = (_, _, _) => Task.FromResult(reasons) };
    }
}
