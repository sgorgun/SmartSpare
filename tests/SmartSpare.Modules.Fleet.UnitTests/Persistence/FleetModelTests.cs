using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartSpare.Modules.Fleet.Domain.Machines;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Infrastructure.Persistence;

namespace SmartSpare.Modules.Fleet;

/// <summary>Rules of the Fleet EF model that need no database.</summary>
public sealed class FleetModelTests
{
    private static FleetDbContext Context(List<string>? warnings = null)
    {
        var builder = new DbContextOptionsBuilder<FleetDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only", npgsql =>
            {
                npgsql.MapEnum<MachineStatus>("machine_status", "fleet");
                npgsql.MapEnum<ReadingSource>("reading_source", "fleet");
                npgsql.MapEnum<ServiceOrderStatus>("service_order_status", "fleet");
            });
        if (warnings is not null)
        {
            builder.LogTo(warnings.Add, [RelationalEventId.BoolWithDefaultWarning]);
        }

        return new FleetDbContext(builder.Options);
    }

    [Fact]
    public void Store_generated_bool_and_enum_defaults_have_sentinels()
    {
        var warnings = new List<string>();
        using var context = Context(warnings);

        _ = context.Model;

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void The_aggregates_are_mapped_with_xmin_and_their_child_collections()
    {
        using var context = Context();
        var model = context.Model;

        model.FindEntityType(typeof(Machine))!.FindProperty("xmin")!.IsConcurrencyToken.ShouldBeTrue();
        model.FindEntityType(typeof(ServiceOrder))!.FindProperty("xmin")!.IsConcurrencyToken.ShouldBeTrue();
        model.FindEntityType(typeof(ServiceOrder))!.FindNavigation(nameof(ServiceOrder.Lines)).ShouldNotBeNull();
        model.FindEntityType(typeof(MaintenancePlan))!.FindNavigation(nameof(MaintenancePlan.Parts)).ShouldNotBeNull();
        model.FindEntityType(typeof(MaintenancePlan))!.FindProperty(nameof(MaintenancePlan.IntervalMonths))!.GetColumnName().ShouldBe("interval_months");
    }
}
