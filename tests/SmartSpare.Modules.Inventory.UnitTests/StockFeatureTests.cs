using SmartSpare.Infrastructure.Http;
using System.Net;
using System.Text;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Features.Stock;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

using SmartSpare.Modules.Inventory.Features;

namespace SmartSpare.Modules.Inventory;

/// <summary>The rules of the stock API that need no database: validation, versions, statuses and the HTTP client.</summary>
public sealed class StockFeatureTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(5, 5.0)]
    [InlineData(5.125, 30.5)]
    public void Valid_stock_levels(decimal safety, double? max) =>
        new ChangeStockLevelsRequestValidator().TestValidate(new ChangeStockLevelsRequest(safety, (decimal?)max)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(-1, null, nameof(ChangeStockLevelsRequest.SafetyStock))]
    [InlineData(1.0001, null, nameof(ChangeStockLevelsRequest.SafetyStock))]
    [InlineData(5, 4.0, nameof(ChangeStockLevelsRequest.MaxStock))]
    public void Invalid_stock_levels_fail_on_their_field(decimal safety, double? max, string field) =>
        new ChangeStockLevelsRequestValidator().TestValidate(new ChangeStockLevelsRequest(safety, (decimal?)max)).ShouldHaveValidationErrorFor(field);

    [Fact]
    public void A_valid_adjustment_passes() =>
        new StockAdjustmentRequestValidator().TestValidate(new StockAdjustmentRequest(3, 1042, -2.5m, "Scrapped")).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(0, 1042, 1, "note", nameof(StockAdjustmentRequest.WarehouseId))]
    [InlineData(3, 0, 1, "note", nameof(StockAdjustmentRequest.PartId))]
    [InlineData(3, 1042, 0, "note", nameof(StockAdjustmentRequest.Quantity))]
    [InlineData(3, 1042, 0.0001, "note", nameof(StockAdjustmentRequest.Quantity))]
    [InlineData(3, 1042, 1, "", nameof(StockAdjustmentRequest.Note))]
    public void Invalid_adjustments_fail_on_their_field(short warehouse, long part, decimal quantity, string note, string field) =>
        new StockAdjustmentRequestValidator().TestValidate(new StockAdjustmentRequest(warehouse, part, quantity, note)).ShouldHaveValidationErrorFor(field);

    [Fact]
    public void A_note_longer_than_the_column_fails() =>
        new StockAdjustmentRequestValidator().TestValidate(new StockAdjustmentRequest(3, 1042, 1, new string('n', 301)))
            .ShouldHaveValidationErrorFor(nameof(StockAdjustmentRequest.Note));

    [Theory]
    [InlineData(null, null, null, 1, 25, true)]
    [InlineData((short)3, "belowSafety", "filter", 2, 100, true)]
    [InlineData((short)0, null, null, 1, 25, false)]
    [InlineData(null, null, null, 1, 101, false)]
    [InlineData(null, null, null, 0, 25, false)]
    public void The_overview_query_is_validated(short? warehouse, string? status, string? search, int page, int pageSize, bool valid) =>
        new StockOverviewQueryValidator().TestValidate(new StockOverviewQuery(warehouse, status, search, page, pageSize)).IsValid.ShouldBe(valid);

    [Fact]
    public void An_unknown_status_filter_is_refused() =>
        new StockOverviewQueryValidator().TestValidate(new StockOverviewQuery(null, "low", null, 1, 25))
            .ShouldHaveValidationErrorFor(nameof(StockOverviewQuery.Status));

    [Fact]
    public void A_ledger_window_must_end_after_it_starts()
    {
        var from = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        new ListMovementsQueryValidator().TestValidate(new ListMovementsQuery(null, null, null, from, from.AddDays(-1), 1, 50))
            .ShouldHaveValidationErrorFor(nameof(ListMovementsQuery.To));
        new ListMovementsQueryValidator().TestValidate(new ListMovementsQuery(null, null, null, from, from.AddDays(1), 1, 50)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("\"7731\"", 7731u)]
    [InlineData("7731", 7731u)]
    [InlineData(" \"0\" ", 0u)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("\"abc\"", null)]
    [InlineData("W/\"12\"", null)]
    [InlineData("-1", null)]
    public void If_match_values_are_parsed_as_versions(string? ifMatch, uint? expected) =>
        Versions.Parse(ifMatch).ShouldBe(expected);

    [Fact]
    public void Versions_round_trip_through_the_entity_tag() =>
        Versions.Parse(Versions.ETag(4_000_000_000)).ShouldBe(4_000_000_000u);

    [Fact]
    public void Every_domain_status_has_its_api_name() =>
        Enum.GetValues<StockStatus>().Select(StockStatusNames.ToApi).ShouldBe(
            [StockStatuses.Out, StockStatuses.BelowSafety, StockStatuses.Ok, StockStatuses.AboveMax]);

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("re90", "%re90%")]
    [InlineData("50%_off\\", @"%50\%\_off\\%")]
    public void Search_text_matches_literally(string? search, string? pattern) =>
        SearchText.Pattern(search).ShouldBe(pattern);

    [Fact]
    public async Task The_warehouse_client_reads_the_internal_endpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """[{"id":1,"code":"KLP","name":"Klaipeda","isCentralHub":true,"isActive":true},{"id":2,"code":"OLD","name":"Old","isCentralHub":false,"isActive":false}]""");
        var queries = new HttpWarehouseQueries(new HttpClient(handler) { BaseAddress = new Uri("http://inventory") });

        var all = await queries.GetAllAsync(TestContext.Current.CancellationToken);

        all.Select(w => w.Code).ShouldBe(["KLP", "OLD"]);
        handler.LastPath.ShouldBe(InventoryInternalApi.Warehouses);
        (await queries.ExistsAsync(1, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await queries.ExistsAsync(2, TestContext.Current.CancellationToken)).ShouldBeFalse("deactivated hubs do not count");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_failing_inventory_service_is_a_dependency_error_not_an_empty_list(HttpStatusCode status)
    {
        var queries = new HttpWarehouseQueries(new HttpClient(new StubHandler(status, "{}")) { BaseAddress = new Uri("http://inventory") });

        var error = await Should.ThrowAsync<DependencyUnavailableException>(() => queries.GetAllAsync(TestContext.Current.CancellationToken));

        error.Service.ShouldBe("inventory");
    }

    [Fact]
    public async Task An_unreachable_inventory_service_is_a_dependency_error()
    {
        var queries = new HttpWarehouseQueries(new HttpClient(new StubHandler(new HttpRequestException("refused"))) { BaseAddress = new Uri("http://inventory") });

        await Should.ThrowAsync<DependencyUnavailableException>(() => queries.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Command_validators_apply_the_request_rules()
    {
        new ChangeStockLevelsCommandValidator().TestValidate(new ChangeStockLevelsCommand(3, 1042, 5, 4, 1)).IsValid.ShouldBeFalse();
        new ChangeStockLevelsCommandValidator().TestValidate(new ChangeStockLevelsCommand(3, 1042, 5, 6, 1)).IsValid.ShouldBeTrue();
        new AdjustStockCommandValidator().TestValidate(new AdjustStockCommand(3, 1042, 0, "note")).IsValid.ShouldBeFalse();
        new AdjustStockCommandValidator().TestValidate(new AdjustStockCommand(3, 1042, -1, "Scrapped")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_levels_without_the_version_read_is_refused_before_the_database()
    {
        await using var db = OfflineContext();
        var handler = new ChangeStockLevelsCommandHandler(db, new EfUnitOfWork<InventoryDbContext>(db));

        var result = await handler.Handle(new ChangeStockLevelsCommand(3, 1042, 5, null, Version: null), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(StockErrors.VersionRequired);
    }

    [Fact]
    public async Task A_keeper_of_another_hub_is_refused_before_the_database()
    {
        await using var db = OfflineContext();
        var keeper = new FixedUser(Guid.NewGuid(), HomeWarehouseId: 5);
        var handler = new AdjustStockCommandHandler(db, new EfUnitOfWork<InventoryDbContext>(db), keeper, TimeProvider.System);

        var result = await handler.Handle(new AdjustStockCommand(3, 1042, -1, "Wrong hub"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InventoryErrors.NotYourWarehouse);
    }

    /// <summary>A context that is never connected: the tested paths return before the first query.</summary>
    private static InventoryDbContext OfflineContext() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql("Host=offline.invalid;Database=none").Options);

    private sealed record FixedUser(Guid? UserId, short? HomeWarehouseId) : ICurrentUser
    {
        public string? Reason => null;

        public IReadOnlyCollection<string> Roles => ["WAREHOUSE_KEEPER"];
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body = string.Empty;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception failure) => _failure = failure;

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return _failure is not null
                ? Task.FromException<HttpResponseMessage>(_failure)
                : Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
