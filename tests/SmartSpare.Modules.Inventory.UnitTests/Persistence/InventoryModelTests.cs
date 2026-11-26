using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory;

/// <summary>Rules of the Inventory EF model that need no database.</summary>
public sealed class InventoryModelTests
{
    [Fact]
    public void Store_generated_bool_and_enum_defaults_have_sentinels()
    {
        // #19: EF Core warns (20601) when a bool or enum with a database default uses the CLR default as its sentinel,
        // because it then cannot tell "not set" from "set to false" or "set to the first member".
        var warnings = new List<string>();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only", npgsql =>
            {
                npgsql.MapEnum<ReservationStatus>("reservation_status", "inventory");
                npgsql.MapEnum<TransferStatus>("transfer_status", "inventory");
                npgsql.MapEnum<ImportSource>("import_source", "raw");
                npgsql.MapEnum<ImportStatus>("import_status", "raw");
            })
            .LogTo(warnings.Add, [RelationalEventId.BoolWithDefaultWarning])
            .Options;

        using var context = new InventoryDbContext(options);
        _ = context.Model;

        warnings.ShouldBeEmpty();
    }
}
