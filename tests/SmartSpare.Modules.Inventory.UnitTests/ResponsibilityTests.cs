using System.Text.Json;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.Modules.Inventory.Domain.Responsibilities;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// Responsible employees of brands and warehouses (ADR 0026 decision 3, STAGE_7_PLAN 1.3 and 3.7): what a replacement
/// changes, when the change reaches Identity, and the contract of <see cref="ResponsibilitiesChanged"/>.
/// </summary>
public sealed class ResponsibilityTests
{
    private static readonly Guid Egle = Guid.Parse("00000000-0000-4000-8000-000000000006");
    private static readonly Guid Laura = Guid.Parse("00000000-0000-4000-8000-000000000104");

    private static bool Grants(string role) => role is "KEEPER" or "SERVICE_BASE" or "BRAND_MANAGER";

    [Fact]
    public void Replacing_the_set_reports_exactly_what_was_removed_and_added()
    {
        var change = ResponsibilityRules.Replace(
            [new ResponsibleEmployee(Egle, "KEEPER"), new ResponsibleEmployee(Laura, "HUB_MANAGER")],
            [new ResponsibleEmployee(Egle, "KEEPER"), new ResponsibleEmployee(Laura, "KEEPER"), new ResponsibleEmployee(Laura, "KEEPER")],
            Grants);

        change.Removed.ShouldBe([new ResponsibleEmployee(Laura, "HUB_MANAGER")]);
        change.Added.ShouldBe([new ResponsibleEmployee(Laura, "KEEPER")], "a duplicate pair counts once");
        change.RightsChanged.ShouldBeTrue();
        change.RightsBearing.Select(r => r.EmployeeId).ShouldBe([Egle, Laura]);
    }

    [Fact]
    public void A_change_of_roles_without_rights_does_not_reach_Identity()
    {
        var change = ResponsibilityRules.Replace(
            [new ResponsibleEmployee(Egle, "KEEPER")],
            [new ResponsibleEmployee(Egle, "KEEPER"), new ResponsibleEmployee(Laura, "HUB_MANAGER")],
            Grants);

        change.Added.ShouldHaveSingleItem();
        change.RightsChanged.ShouldBeFalse("a hub manager gets no hub access, so no session changes");
        change.RightsBearing.ShouldBe([new ResponsibleEmployee(Egle, "KEEPER")]);
    }

    [Fact]
    public void The_same_set_again_changes_nothing()
    {
        var current = new[] { new ResponsibleEmployee(Egle, "KEEPER") };

        var change = ResponsibilityRules.Replace(current, current, Grants);

        (change.Removed.Count, change.Added.Count, change.RightsChanged).ShouldBe((0, 0, false));
    }

    [Fact]
    public void Removing_the_last_keeper_reaches_Identity_with_an_empty_set()
    {
        var change = ResponsibilityRules.Replace([new ResponsibleEmployee(Egle, "KEEPER")], [], Grants);

        change.RightsChanged.ShouldBeTrue();
        change.RightsBearing.ShouldBeEmpty("Identity replaces the record's rows with nothing");
    }

    [Fact]
    public void ResponsibilitiesChanged_round_trips_with_the_full_set_and_the_acting_user()
    {
        var admin = Guid.Parse("00000000-0000-4000-8000-000000000011");
        var sent = new ResponsibilitiesChanged(ResponsibilitiesChanged.Warehouse, 3, [new ResponsibleAssignment(Egle, "KEEPER")], admin)
        { OccurredAt = new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero) };

        var message = IntegrationEventSerializer.ToOutboxMessage(sent);
        var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBeOfType<ResponsibilitiesChanged>();

        message.Type.ShouldBe("SmartSpare.Modules.Inventory.Contracts.ResponsibilitiesChanged, SmartSpare.Modules.Inventory.Contracts");
        (received with { Assignments = sent.Assignments }).ShouldBe(sent);
        received.Assignments.ShouldBe(sent.Assignments);
        JsonDocument.Parse(message.Payload).RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["id", "occurredAt", "recordKind", "recordId", "assignments", "actorId"], ignoreOrder: true);
        received.ShouldBeAssignableTo<IActedByUser>()!.ActorId.ShouldBe(admin);
    }
}
