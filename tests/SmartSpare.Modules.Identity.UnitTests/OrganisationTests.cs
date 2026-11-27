using SmartSpare.Modules.Identity.Domain.Organisation;

namespace SmartSpare.Modules.Identity;

/// <summary>Rights derivation and the organisation aggregates (ADR 0026, STAGE_7_PLAN sections 1.1, 1.2 and 1.7).</summary>
public sealed class OrganisationTests
{
    private static readonly ScopeFact Claas = new(1, "CLAAS", "Brand manager of CLAAS");
    private static readonly ScopeFact Jd = new(2, "JD", "Brand manager of JD");
    private static readonly ScopeFact Klp = new(3, "KLP", "Keeper of KLP");
    private static readonly ScopeFact Pnv = new(4, "PNV", "Service base PNV");

    private static RightsInput Input(string[] roles, ScopeFact[]? brands = null, ScopeFact[]? hubs = null, bool account = true, bool employee = true) =>
        new(account, employee, "BRAND_MANAGER", "Brand manager", roles, brands ?? [], hubs ?? []);

    [Fact]
    public void Roles_are_exactly_the_template_of_the_position_with_the_position_as_origin()
    {
        var rights = EffectiveRightsCalculator.Calculate(Input(["SPONSOR", "BRAND_MANAGER", "SPONSOR"]));

        rights.Roles.ShouldBe(["BRAND_MANAGER", "SPONSOR"]);
        rights.Rights.ShouldAllBe(r => r.Origin == RightOrigin.Position && r.OriginName == "Brand manager");
        rights.InactiveReason.ShouldBeNull();
    }

    [Fact]
    public void A_brand_assignment_gives_scope_only_with_the_brand_manager_role()
    {
        var manager = EffectiveRightsCalculator.Calculate(Input(["BRAND_MANAGER"], [Jd, Claas]));
        manager.BrandIds.ShouldBe([(short)1, 2]);
        manager.Rights.Where(r => r.Kind == RightKind.Brand).Select(r => r.OriginName).ShouldBe(["Brand manager of CLAAS", "Brand manager of JD"]);
        manager.WithoutEffect.ShouldBeEmpty();

        var planner = EffectiveRightsCalculator.Calculate(Input(["INVENTORY_PLANNER"], [Claas]));
        planner.BrandIds.ShouldBeEmpty();
        planner.WithoutEffect.ShouldHaveSingleItem().Value.ShouldBe("CLAAS");
    }

    [Fact]
    public void Hub_access_comes_from_warehouse_assignments_and_may_be_several_hubs()
    {
        var rights = EffectiveRightsCalculator.Calculate(Input(["WAREHOUSE_KEEPER"], hubs: [Pnv, Klp, Klp]));

        rights.HubIds.ShouldBe([(short)3, 4]);
        rights.Rights.Where(r => r.Kind == RightKind.Hub).ShouldAllBe(r => r.Origin == RightOrigin.WarehouseAssignment);
    }

    [Theory]
    [InlineData(false, true, "The employee has left or was anonymised.")]
    [InlineData(true, false, "The account is locked by an administrator.")]
    [InlineData(false, false, "The employee has left or was anonymised.")]
    public void An_employee_who_left_or_a_locked_account_has_no_rights_at_all(bool employee, bool account, string reason)
    {
        var rights = EffectiveRightsCalculator.Calculate(Input(["ADMINISTRATOR"], [Claas], [Klp], account, employee));

        rights.Rights.ShouldBeEmpty();
        rights.WithoutEffect.Count.ShouldBe(3, "the facts stay visible on the admin screen");
        rights.InactiveReason.ShouldBe(reason);
    }

    [Fact]
    public void An_empty_template_gives_no_role()
    {
        EffectiveRightsCalculator.Calculate(Input([])).Rights.ShouldBeEmpty();
    }

    [Fact]
    public void A_position_reports_whether_its_template_changed()
    {
        var position = Position.Create(" brand_manager ", "Brand manager", null, [1]).Value;
        position.Code.ShouldBe("BRAND_MANAGER");

        position.Update("Brand manager", "Brands", [1]).Value.ShouldBeFalse("same template: no session has to change");
        position.Update("Brand manager", null, [1, 5]).Value.ShouldBeTrue();
        position.RoleIds.ShouldBe(new HashSet<short> { 1, 5 });
        position.Update("Brand manager", null, [5]).Value.ShouldBeTrue();
        position.RoleIds.ShouldBe(new HashSet<short> { 5 });
    }

    [Theory]
    [InlineData("x", "Name")]
    [InlineData("WITH SPACE", "Name")]
    [InlineData("OK_CODE", "")]
    [InlineData("OK_CODE", "  ")]
    public void A_position_needs_a_valid_code_and_name(string code, string name) =>
        Position.Create(code, name, null, []).IsFailure.ShouldBeTrue();

    [Fact]
    public void Archiving_and_restoring_a_position_keeps_who_and_when()
    {
        var position = Position.Create("PARTS_ADVISOR", "Parts advisor", null, []).Value;
        var by = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

        position.Archive(now, by);
        (position.IsArchived, position.ArchivedAt, position.ArchivedBy).ShouldBe((true, now, by));
        position.Restore();
        (position.IsArchived, position.ArchivedAt, position.ArchivedBy).ShouldBe((false, (DateTimeOffset?)null, (Guid?)null));
    }

    [Fact]
    public void An_employee_reports_a_new_position_and_keeps_the_number()
    {
        var employee = Employee.Create(Guid.NewGuid(), "e0042", new EmployeeDetails("Asta Vaitkienė", "asta@dealer.example", null, 2, new DateOnly(2026, 1, 5))).Value;
        employee.Code.ShouldBe("E0042");
        employee.IsActive.ShouldBeTrue();

        employee.Update(new EmployeeDetails("Asta Vaitkienė", null, "+370 600 00042", 2, null)).Value.ShouldBeFalse();
        employee.Email.ShouldBeNull();
        employee.Update(new EmployeeDetails("Asta Vaitkienė", null, null, 3, null)).Value.ShouldBeTrue();
        employee.PositionId.ShouldBe((short)3);
    }

    [Fact]
    public void An_employee_leaves_on_a_date_not_before_hiring_and_can_be_restored()
    {
        var employee = Employee.Create(Guid.NewGuid(), "E0043", new EmployeeDetails("Jonas", null, null, 2, new DateOnly(2026, 6, 1))).Value;

        employee.Archive(new DateOnly(2026, 5, 31), DateTimeOffset.UtcNow, null).IsFailure.ShouldBeTrue();
        employee.Archive(new DateOnly(2026, 12, 31), DateTimeOffset.UtcNow, null).IsSuccess.ShouldBeTrue();
        employee.IsActive.ShouldBeFalse();
        employee.LeftOn.ShouldBe(new DateOnly(2026, 12, 31));

        employee.Restore().IsSuccess.ShouldBeTrue();
        employee.IsActive.ShouldBeTrue();
        employee.LeftOn.ShouldBeNull();
    }

    [Theory]
    [InlineData("42", "Name", null)]
    [InlineData("E12", "Name", null)]
    [InlineData("E0001", "", null)]
    [InlineData("E0001", "Name", "not an address")]
    public void An_employee_needs_a_number_a_name_and_a_valid_email(string code, string name, string? email) =>
        Employee.Create(Guid.NewGuid(), code, new EmployeeDetails(name, email, null, 1, null)).IsFailure.ShouldBeTrue();

    [Fact]
    public void An_account_belongs_to_an_employee()
    {
        Should.Throw<ArgumentException>(() => Domain.AppUser.Create(
            Guid.NewGuid(), Guid.Empty, Domain.UserName.Create("abc").Value, "AQAAAAIAA1tgAAAAE" + new string('A', 67)));
    }
}
