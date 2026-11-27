using System.Reflection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Modules.Identity.Domain;

namespace SmartSpare.Modules.Identity;

/// <summary>Roles as data (ADR 0028): the aggregate and the separation-of-duties list.</summary>
public sealed class RoleTests
{
    [Fact]
    public void A_role_is_a_named_bundle_of_catalogue_permissions()
    {
        var role = Role.Create(" purchasing_clerk ", " Purchasing clerk ", "  ", [Permissions.Procurement.PurchaseOrder.Create, Permissions.Inventory.Part.View], Permissions.Exists).Value;

        (role.Code, role.Name, role.Description, role.IsLocked, role.IsArchived).ShouldBe(("PURCHASING_CLERK", "Purchasing clerk", (string?)null, false, false));
        role.PermissionNames.ShouldBe([Permissions.Inventory.Part.View, Permissions.Procurement.PurchaseOrder.Create], ignoreOrder: true);
    }

    [Theory]
    [InlineData("AB", "Name", "Identity.InvalidRoleCode")]
    [InlineData("WITH SPACE", "Name", "Identity.InvalidRoleCode")]
    [InlineData("ROLE1", "Name", "Identity.InvalidRoleCode")]
    [InlineData("GOOD_CODE", " ", "Identity.InvalidName")]
    public void An_invalid_role_is_refused(string code, string name, string error) =>
        Role.Create(code, name, null, [], Permissions.Exists).Error.Code.ShouldBe(error);

    [Fact]
    public void Only_catalogue_permissions_can_be_granted() =>
        Role.Create("CLERK", "Clerk", null, ["Inventory.Part.Delete"], Permissions.Exists).Error.ShouldBe(RoleErrors.UnknownPermission);

    [Fact]
    public void An_update_reports_whether_the_permissions_changed()
    {
        var role = Role.Create("CLERK", "Clerk", null, [Permissions.Inventory.Part.View], Permissions.Exists).Value;

        role.Update("Clerk at the counter", null, [Permissions.Inventory.Part.View], Permissions.Exists).Value.ShouldBeFalse("only the name changed");
        role.Update("Clerk at the counter", null, [Permissions.Inventory.Part.View, Permissions.Inventory.Stock.View], Permissions.Exists).Value.ShouldBeTrue();
        role.Update("Clerk at the counter", null, [Permissions.Inventory.Stock.View], Permissions.Exists).Value.ShouldBeTrue();
        role.PermissionNames.ShouldBe([Permissions.Inventory.Stock.View]);
    }

    [Fact]
    public void A_locked_role_cannot_be_changed_or_archived()
    {
        var role = Role.Create("ADMINISTRATOR", "Administrator", null, [Permissions.Organisation.Role.Edit], Permissions.Exists).Value;
        typeof(Role).GetProperty(nameof(Role.IsLocked))!.SetValue(role, true, BindingFlags.NonPublic | BindingFlags.Instance, null, null, null);

        role.Update("Anything", null, [], Permissions.Exists).Error.ShouldBe(RoleErrors.Locked);
        role.Archive().Error.ShouldBe(RoleErrors.Locked);
        role.PermissionNames.ShouldBe([Permissions.Organisation.Role.Edit]);
    }

    [Fact]
    public void An_archived_role_can_be_restored()
    {
        var role = Role.Create("CLERK", "Clerk", null, [], Permissions.Exists).Value;

        role.Archive().IsSuccess.ShouldBeTrue();
        role.IsArchived.ShouldBeTrue();
        role.Restore();
        role.IsArchived.ShouldBeFalse();
    }

    [Fact]
    public void Separation_of_duties_finds_a_complete_pair_only()
    {
        DutyConflicts.FindIn([Permissions.Procurement.PurchaseOrder.Create]).ShouldBeNull();
        DutyConflicts.FindIn([Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.Supplier.Edit, Permissions.Inventory.Part.View])
            .ShouldBe((Permissions.Procurement.PurchaseOrder.Create, Permissions.Procurement.Supplier.Edit));
        DutyConflicts.Pairs.ShouldAllBe(p => Permissions.Exists(p.First) && Permissions.Exists(p.Second), "every pair names catalogue permissions");
    }
}
