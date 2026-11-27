using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Fleet;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Fleet.Domain.ServiceOrders;
using SmartSpare.Modules.Fleet.Features.ServiceOrders;

namespace SmartSpare.Modules.Fleet;

/// <summary>Service orders as the API shows them (STAGE_6_PLAN 4, 6): kit status, statuses, allowed actions, input rules.</summary>
public sealed class ServiceOrderFeatureTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

    // ServiceOrderStatus: 0 draft, 1 parts reserved, 2 in progress, 3 completed, 4 cancelled.
    // KitStatus: 0 reserving, 1 reserved, 2 partial, 3 not held, 4 issued, 5 released, 6 no parts.
    [Theory]
    [InlineData(3, true, 2, 0, 2, 4)]
    [InlineData(4, true, 2, 2, 0, 5)]
    [InlineData(0, false, 0, 0, 0, 6)]
    [InlineData(0, false, 2, 2, 0, 0)]
    [InlineData(1, true, 2, 0, 2, 1)]
    [InlineData(0, true, 2, 1, 1, 2)]
    [InlineData(2, true, 2, 2, 0, 3)]
    public void The_kit_status_follows_the_order_and_the_held_lines(int status, bool answered, int lines, int shortLines, int heldLines, int expected) =>
        ServiceOrder.KitOf((ServiceOrderStatus)status, answered ? Now : null, lines, shortLines, heldLines).ShouldBe((KitStatus)expected);

    [Fact]
    public void A_new_draft_is_reserving_until_Inventory_answers()
    {
        var order = ServiceOrder.DraftFromPlan(501, 21, 5, 500, 512, Engineer, [new(1001, 1)]).Value;

        order.Kit.ShouldBe(KitStatus.Reserving);
        order.ApplyKitReserved(new Dictionary<long, decimal>(), Now);
        order.Kit.ShouldBe(KitStatus.NotHeld, "answered, nothing held");
        order.KitReservedAt.ShouldBe(Now);
    }

    [Fact]
    public void Statuses_map_between_the_database_and_the_api()
    {
        ServiceOrderMapping.ApiStatus("parts_reserved").ShouldBe(ServiceOrderStatuses.PartsReserved);
        ServiceOrderMapping.ApiStatus("in_progress").ShouldBe(ServiceOrderStatuses.InProgress);
        ServiceOrderMapping.ApiStatus(ServiceOrderStatus.Cancelled).ShouldBe("cancelled");
        ServiceOrderMapping.SqlStatus(ServiceOrderStatuses.PartsReserved).ShouldBe("parts_reserved");
        ServiceOrderMapping.SqlStatus(null).ShouldBeNull();
        foreach (var status in ServiceOrderStatuses.All)
        {
            ServiceOrderMapping.ApiStatus(ServiceOrderMapping.SqlStatus(status)!).ShouldBe(status);
        }

        ServiceOrderMapping.KitStatusName(KitStatus.NotHeld).ShouldBe(KitStatuses.NotHeld);
        ServiceOrderMapping.KitStatusName(KitStatus.Reserving).ShouldBe(KitStatuses.Reserving);
        ServiceOrderMapping.KitOf("draft", null, 1, 1, 0).ShouldBe(KitStatus.Reserving);
    }

    [Theory]
    [InlineData(AppRoles.ServiceEngineer, true)]
    [InlineData(AppRoles.Administrator, true)]
    [InlineData(AppRoles.BrandManager, false)]
    [InlineData(AppRoles.Sponsor, false)]
    public void Start_is_offered_to_the_roles_of_RecordServiceWork_while_the_order_has_not_started(string role, bool offered)
    {
        var access = new ServiceOrderAccess(new Roles(role));
        var draft = ServiceOrder.DraftFromPlan(501, 21, 5, 500, 512, Engineer, [new(1001, 1)]).Value;

        access.AllowedActions(draft).Contains(ServiceOrderActions.Start).ShouldBe(offered);
        draft.Start();
        access.AllowedActions(draft).ShouldNotContain(ServiceOrderActions.Start);
    }

    [Fact]
    public void A_new_order_is_either_a_planned_service_or_a_repair_with_valid_parts()
    {
        var validator = new CreateServiceOrderRequestValidator();

        validator.Validate(new CreateServiceOrderRequest(501, MaintenancePlanId: 21)).IsValid.ShouldBeTrue();
        validator.Validate(new CreateServiceOrderRequest(501, Lines: [new(1001, 2), new(1002, 0.5m)])).IsValid.ShouldBeTrue();
        validator.Validate(new CreateServiceOrderRequest(501)).IsValid.ShouldBeFalse("neither plan nor parts");
        validator.Validate(new CreateServiceOrderRequest(501, 21, [new(1001, 1)])).IsValid.ShouldBeFalse("both plan and parts");
        validator.Validate(new CreateServiceOrderRequest(501, Lines: [new(1001, 0)])).IsValid.ShouldBeFalse();
        validator.Validate(new CreateServiceOrderRequest(501, Lines: [new(1001, 1.0005m)])).IsValid.ShouldBeFalse();
        validator.Validate(new CreateServiceOrderRequest(501, Lines: [new(1001, 1), new(1001, 2)])).IsValid.ShouldBeFalse("duplicate part");
        validator.Validate(new CreateServiceOrderRequest(0, MaintenancePlanId: 21)).IsValid.ShouldBeFalse();
        validator.Validate(new CreateServiceOrderRequest(501, Lines: [.. Enumerable.Range(1, 51).Select(i => new CreateServiceOrderLine(i, 1))]))
            .IsValid.ShouldBeFalse("at most 50 lines");
    }

    [Fact]
    public void The_list_validates_its_filters()
    {
        var validator = new ListServiceOrdersQueryValidator();

        validator.Validate(new ListServiceOrdersQuery("partsReserved", 5, 501, 1, 25)).IsValid.ShouldBeTrue();
        validator.Validate(new ListServiceOrdersQuery("parts_reserved", 0, 0, 0, 101)).Errors.Select(e => e.PropertyName)
            .ShouldBe(["Status", "WarehouseId", "MachineId", "Page", "PageSize"], ignoreOrder: true);
    }

    private sealed class Roles(params string[] roles) : ICurrentUser
    {
        public Guid? UserId => Engineer;

        public string? Reason => null;

        IReadOnlyCollection<string> ICurrentUser.Roles => roles;

        IReadOnlyCollection<string> ICurrentUser.Permissions => [.. SmartSpare.ApiContracts.Authorization.DefaultRoles.Of(roles)];

        public IReadOnlyCollection<short> WarehouseIds => [];
    }
}
