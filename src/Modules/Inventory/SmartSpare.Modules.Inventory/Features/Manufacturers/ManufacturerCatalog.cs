using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.ApiContracts.MasterData;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory.Features.Manufacturers;

/// <summary>
/// The manufacturer catalogue under <c>/api/v1/inventory/manufacturers</c> (STAGE_7_PLAN 3.1, PR 7h3), built by the
/// shared master-data mechanism (ADR 0025). Only Inventory uses manufacturers, so nothing is published. A manufacturer
/// with active parts or responsible employees cannot be archived (SS501).
/// </summary>
internal static class ManufacturerCatalog
{
    public static CatalogDefinition<Manufacturer, short, ManufacturerDto, ManufacturerWrite> Definition { get; } = new()
    {
        Route = "/manufacturers",
        ApiBase = "/api/v1/inventory",
        Tag = "Manufacturers",
        Noun = "manufacturer",
        OperationNoun = "Manufacturer",
        ViewPermission = Permissions.Inventory.Manufacturer.View,
        CreatePermission = Permissions.Inventory.Manufacturer.Create,
        EditPermission = Permissions.Inventory.Manufacturer.Edit,
        ArchivePermission = Permissions.Inventory.Manufacturer.Archive,
        AuditTable = "catalog." + Infrastructure.Persistence.Configurations.ManufacturerConfiguration.Table,
        Projection = m => new ManufacturerDto(m.Id, m.Code, m.Name, m.CountryCode, m.Website, m.IsArchived, Versions.Format(EF.Property<uint>(m, "xmin"))),
        VersionOf = m => m.Version,
        IdOf = m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Lookup = m => new CatalogLookupItem(m.Id, m.Code, m.Name, m.CountryCode),
        NormaliseCode = Manufacturer.NormaliseCode,
        Create = w => Manufacturer.Create(w.Code, w.Name, w.CountryCode, w.Website),
        Update = (m, w) => m.Update(w.Name, w.CountryCode, w.Website),
        ArchiveGuard = ArchiveGuardAsync,
        Validator = new ManufacturerWriteValidator(),
        Csv = new CatalogCsv<ManufacturerDto, ManufacturerWrite>
        {
            Columns = ["code", "name", "country_code", "website"],
            ToRow = m => [m.Code, m.Name, m.CountryCode, m.Website],
            FromRow = row => new ManufacturerWrite(row.Text("code") ?? string.Empty, row.Text("name") ?? string.Empty, row.Text("country_code"), row.Text("website")),
            ExampleRow = ["MANN", "MANN+HUMMEL", "DE", "https://www.mann-hummel.com"],
        },
        FieldLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = "Code",
            ["name"] = "Name",
            ["country_code"] = "Country",
            ["website"] = "Website",
        },
        Rules = "Permissions Inventory.Manufacturer.View to read and .Create, .Edit, .Archive to change (default roles: every role reads; planners and administrators change). "
            + "A manufacturer with active parts cannot be archived.",
        Example = new ManufacturerDto(1, "MANN", "MANN+HUMMEL", "DE", "https://www.mann-hummel.com", false, "7710"),
        WriteExample = new ManufacturerWrite("MANN", "MANN+HUMMEL", "DE", "https://www.mann-hummel.com"),
        LookupExample = new CatalogLookupItem(1, "MANN", "MANN+HUMMEL", "DE"),
    };

    public static void Map(IEndpointRouteBuilder v1) =>
        v1.MapCatalog<InventoryDbContext, Manufacturer, short, ManufacturerDto, ManufacturerWrite>(Definition);

    private static async Task<IReadOnlyList<string>> ArchiveGuardAsync(IServiceProvider services, Manufacturer manufacturer, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<InventoryDbContext>();
        var parts = await db.Set<Part>().AsNoTracking().CountAsync(p => p.ManufacturerId == manufacturer.Id && p.IsActive, cancellationToken).ConfigureAwait(false);
        var responsibles = await db.Set<Domain.Responsibilities.ManufacturerResponsible>().AsNoTracking()
            .CountAsync(r => r.ManufacturerId == manufacturer.Id, cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();
        if (parts > 0)
        {
            reasons.Add($"{parts} active part(s)");
        }

        if (responsibles > 0)
        {
            reasons.Add($"{responsibles} responsible employee(s); remove them first");
        }

        return reasons;
    }
}
