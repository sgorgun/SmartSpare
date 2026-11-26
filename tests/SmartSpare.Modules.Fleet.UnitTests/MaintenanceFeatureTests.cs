using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Modules.Fleet.Domain.Maintenance;
using SmartSpare.Modules.Fleet.Features.Machines;
using SmartSpare.Modules.Fleet.Features.Maintenance;

namespace SmartSpare.Modules.Fleet;

/// <summary>How the due calculation reaches the API: mapping, urgency order, the due-list filter and the query rules.</summary>
public sealed class MaintenanceFeatureTests
{
    private static MaintenancePlanDue Due(string status, DateOnly? dueDate, decimal remaining = 100) =>
        new(1, "Engine service", 500, 12, 500, 1000, remaining, dueDate, null, dueDate, MaintenanceDueTriggers.EngineHours, status, null);

    [Fact]
    public void A_calculation_maps_every_field_and_names_status_and_trigger()
    {
        var input = new MaintenanceDueInput(500, 12, 1.0m, 512, 440, 500, 500, null, new DateOnly(2025, 6, 15),
            [new ReadingPoint(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero), 440), new ReadingPoint(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero), 512)]);
        var due = MaintenanceDueCalculator.Calculate(input, new DateOnly(2026, 11, 26));

        var dto = MaintenanceDueMapping.ToDto(21, "Engine service", input, due);

        dto.ShouldBe(new MaintenancePlanDue(21, "Engine service", 500, 12, 500.0m, 1000m, 488m, due.ProjectedDueDate,
            new DateOnly(2026, 6, 15), due.DueDate, MaintenanceDueTriggers.Calendar, MaintenanceDueStatuses.Planned, 500m));
    }

    [Fact]
    public void Statuses_have_their_api_names()
    {
        MaintenanceDueMapping.StatusName(MaintenanceDueStatus.Overdue).ShouldBe("overdue");
        MaintenanceDueMapping.StatusName(MaintenanceDueStatus.DueSoon).ShouldBe("dueSoon");
        MaintenanceDueMapping.StatusName(MaintenanceDueStatus.Planned).ShouldBe("planned");
        MaintenanceDueMapping.StatusName(MaintenanceDueStatus.Ok).ShouldBe("ok");
    }

    [Fact]
    public void Triggers_have_their_api_names()
    {
        MaintenanceDueMapping.TriggerName(MaintenanceDueTrigger.Calendar).ShouldBe("calendar");
        MaintenanceDueMapping.TriggerName(MaintenanceDueTrigger.EngineHours).ShouldBe("engineHours");
    }

    [Fact]
    public void Plans_are_ordered_by_urgency_then_due_date_then_remaining_hours()
    {
        var day = new DateOnly(2026, 12, 1);
        var plans = new[]
        {
            Due(MaintenanceDueStatuses.Ok, day), Due(MaintenanceDueStatuses.DueSoon, null, 40), Due(MaintenanceDueStatuses.Overdue, day.AddDays(3)),
            Due(MaintenanceDueStatuses.DueSoon, day), Due(MaintenanceDueStatuses.Planned, day), Due(MaintenanceDueStatuses.Overdue, day, 5),
            Due(MaintenanceDueStatuses.Overdue, day, -10),
        };

        MaintenanceDueMapping.ByUrgency(plans, p => p).Select(p => (p.Status, p.DueDate, p.RemainingHours)).ShouldBe(
        [
            ("overdue", (DateOnly?)day, -10m), ("overdue", day, 5m), ("overdue", day.AddDays(3), 100m), ("dueSoon", day, 100m),
            ("dueSoon", null, 40m), ("planned", day, 100m), ("ok", day, 100m),
        ]);
        MaintenanceDueMapping.Urgency("unknown").ShouldBe(4);
    }

    [Fact]
    public void The_due_list_is_ordered_by_due_date_with_unknown_dates_last()
    {
        var day = new DateOnly(2026, 12, 1);
        var rows = new[] { Due("dueSoon", null, 10), Due("dueSoon", day.AddDays(2)), Due("overdue", day), Due("dueSoon", day) };

        MaintenanceDueMapping.ByDueDate(rows, r => r).Select(r => (r.Status, r.DueDate)).ShouldBe(
            [("overdue", (DateOnly?)day), ("dueSoon", day), ("dueSoon", day.AddDays(2)), ("dueSoon", null)]);
    }

    [Fact]
    public void The_due_list_shows_overdue_and_due_soon_unless_one_status_is_asked_for()
    {
        MaintenanceDueMapping.ListedStatuses(null).ShouldBe(["overdue", "dueSoon"], ignoreOrder: true);
        MaintenanceDueMapping.ListedStatuses("planned").ShouldBe(["planned"]);
    }

    [Fact]
    public void The_list_queries_validate_their_filters()
    {
        new ListMachinesQueryValidator().Validate(new ListMachinesQuery("LX9", 5, "active", "overdue", 1, 25)).IsValid.ShouldBeTrue();
        new ListMachinesQueryValidator().Validate(new ListMachinesQuery(new string('x', 41), 0, "sold", "late", 0, 101)).Errors
            .Select(e => e.PropertyName).ShouldBe(["Search", "WarehouseId", "Status", "DueStatus", "Page", "PageSize"], ignoreOrder: true);

        new DueMaintenanceQueryValidator().Validate(new DueMaintenanceQuery(null, 14, null, 1, 25)).IsValid.ShouldBeTrue();
        new DueMaintenanceQueryValidator().Validate(new DueMaintenanceQuery(-1, 366, "late", 0, 0)).Errors
            .Select(e => e.PropertyName).ShouldBe(["WarehouseId", "HorizonDays", "Status", "Page", "PageSize"], ignoreOrder: true);

        new ListReadingsQueryValidator().Validate(new ListReadingsQuery(1, 1, 100)).IsValid.ShouldBeTrue();
        new ListReadingsQueryValidator().Validate(new ListReadingsQuery(1, 0, 101)).Errors.Count.ShouldBe(2);
    }
}
