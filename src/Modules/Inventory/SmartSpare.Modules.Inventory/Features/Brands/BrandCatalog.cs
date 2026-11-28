using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Csv;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Brands;

/// <summary>
/// The brand catalogue under <c>/api/v1/inventory/brands</c> (STAGE_7_PLAN 3.1, PR 7h2), built by the shared master-data
/// mechanism (ADR 0025). Every write publishes <see cref="Contracts.BrandCatalogChanged"/> in its transaction, so Fleet's
/// and Identity's copies follow. A brand with active parts or active responsibles cannot be archived (SS501).
/// </summary>
internal static class BrandCatalog
{
    public static CatalogDefinition<Brand, short, BrandDto, BrandWrite> Definition { get; } = new()
    {
        Route = "/brands",
        ApiBase = "/api/v1/inventory",
        Tag = "Brands",
        Noun = "brand",
        OperationNoun = "Brand",
        ViewPermission = Permissions.Inventory.Brand.View,
        CreatePermission = Permissions.Inventory.Brand.Create,
        EditPermission = Permissions.Inventory.Brand.Edit,
        ArchivePermission = Permissions.Inventory.Brand.Archive,
        AuditTable = "catalog." + Infrastructure.Persistence.Configurations.BrandConfiguration.Table,
        Projection = b => new BrandDto(b.Id, b.Code, b.Name, b.CountryCode, b.IsArchived, Versions.Format(EF.Property<uint>(b, "xmin"))),
        VersionOf = b => b.Version,
        IdOf = b => b.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Lookup = b => new CatalogLookupItem(b.Id, b.Code, b.Name, b.CountryCode),
        NormaliseCode = Brand.NormaliseCode,
        Create = w => Brand.Create(w.Code, w.Name, w.CountryCode),
        Update = (b, w) => b.Update(w.Name, w.CountryCode),
        ArchiveGuard = ArchiveGuardAsync,
        Publish = (_, unitOfWork, brands, _) =>
        {
            BrandCatalogPublication.Queue(unitOfWork, [.. brands.Select(BrandCatalogPublication.Snapshot)]);
            return Task.CompletedTask;
        },
        Validator = new BrandWriteValidator(),
        Csv = new CatalogCsv<BrandDto, BrandWrite>
        {
            Columns = ["code", "name", "country_code"],
            ToRow = b => [b.Code, b.Name, b.CountryCode],
            FromRow = row => new BrandWrite(row.Text("code") ?? string.Empty, row.Text("name") ?? string.Empty, row.Text("country_code") ?? string.Empty),
            ExampleRow = ["CLAAS", "CLAAS", "DE"],
        },
        FieldLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = "Code",
            ["name"] = "Name",
            ["country_code"] = "Country",
        },
        Rules = "Permissions Inventory.Brand.View to read and .Create, .Edit, .Archive to change (default roles: planners and administrators change). "
            + "A brand with active parts or active responsibles cannot be archived.",
        Example = new BrandDto(1, "CLAAS", "CLAAS", "DE", false, "9120"),
        WriteExample = new BrandWrite("CLAAS", "CLAAS", "DE"),
        LookupExample = new CatalogLookupItem(1, "CLAAS", "CLAAS", "DE"),
    };

    public static void Map(IEndpointRouteBuilder v1) =>
        v1.MapCatalog<InventoryDbContext, Brand, short, BrandDto, BrandWrite>(Definition);

    /// <summary>SS501 reasons: active parts of the brand, and employees responsible for it (product owner, 28 November 2026).</summary>
    private static async Task<IReadOnlyList<string>> ArchiveGuardAsync(IServiceProvider services, Brand brand, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<InventoryDbContext>();
        var parts = await db.Set<Part>().AsNoTracking().CountAsync(p => p.BrandId == brand.Id && !p.IsArchived, cancellationToken).ConfigureAwait(false);
        var responsibles = await db.Set<BrandResponsible>().AsNoTracking().CountAsync(r => r.BrandId == brand.Id, cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();
        if (parts > 0)
        {
            reasons.Add($"{parts} active part(s)");
        }

        if (responsibles > 0)
        {
            reasons.Add($"{responsibles} responsible employee(s); remove them on the Responsibilities page first");
        }

        return reasons;
    }
}
