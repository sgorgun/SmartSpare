using System.Text.Json;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory;

/// <summary>The contract of <see cref="ServiceKitIssued"/> (STAGE_6_PLAN 5.4): issued and shortfall per line, the acting user.</summary>
public sealed class KitIssueContractTests
{
    private static readonly Guid Engineer = Guid.Parse("00000000-0000-4000-8000-000000000004");

    [Fact]
    public void ServiceKitIssued_round_trips_with_issued_and_shortfall_per_line()
    {
        var sent = new ServiceKitIssued(9001, [new IssuedKitLine(71, 24, 6), new IssuedKitLine(72, 1, 0)], Engineer)
        { OccurredAt = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero) };

        var message = IntegrationEventSerializer.ToOutboxMessage(sent);
        var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload).ShouldBeOfType<ServiceKitIssued>();

        message.Type.ShouldBe("SmartSpare.Modules.Inventory.Contracts.ServiceKitIssued, SmartSpare.Modules.Inventory.Contracts");
        (received with { Lines = sent.Lines }).ShouldBe(sent);
        received.Lines.ShouldBe(sent.Lines);
        var json = JsonDocument.Parse(message.Payload).RootElement;
        json.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "occurredAt", "serviceOrderId", "lines", "actorId"], ignoreOrder: true);
        json.GetProperty("lines")[0].GetProperty("shortfallQty").GetDecimal().ShouldBe(6);
        received.ShouldBeAssignableTo<IActedByUser>()!.ActorId.ShouldBe(Engineer);
    }
}
