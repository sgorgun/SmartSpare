using System.Text.Json;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Modules.Inventory;

/// <summary>
/// The integration event contracts of Inventory (MODULE_MAPPING 3.5). They are defined before their first consumer and
/// written to the outbox only from the stage that adds it (STAGE_5_PLAN section 4.1), so these tests pin the wire format
/// the consumers of stages 6 and 8 will read: the outbox type name and the JSON of the payload, round trip included.
/// </summary>
public sealed class EventContractTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 26, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid Planner = Guid.Parse("6f1c2a9e-3b7d-4c11-9a52-0d8e4b7f3a21");

    public static TheoryData<string> EventTypes =>
        [nameof(TransferApproved), nameof(TransferCancelled), nameof(TransferRejected), nameof(PartCatalogChanged), nameof(ServiceKitReserved), nameof(ServiceKitReservationExpired)];

    [Fact]
    public void TransferApproved_round_trips_through_the_outbox_with_a_stable_shape()
    {
        var sent = new TransferApproved(412, "TR-2026-000412", 3, 5, IsUrgent: false, RecommendationId: 77,
            [new TransferredPart(1042, 11), new TransferredPart(1043, 2.5m)], Planner, At)
        { OccurredAt = At };

        var (received, json) = RoundTrip(sent);

        var approved = received.ShouldBeOfType<TransferApproved>();
        (approved with { Lines = sent.Lines }).ShouldBe(sent, "every scalar member survives the round trip");
        approved.Lines.ShouldBe(sent.Lines);
        PropertyNames(json).ShouldBe(
            ["id", "occurredAt", "transferOrderId", "number", "fromWarehouseId", "toWarehouseId", "isUrgent", "recommendationId",
             "lines", "approvedBy", "approvedAt"], ignoreOrder: true);
        json.GetProperty("lines")[1].GetProperty("quantity").GetDecimal().ShouldBe(2.5m);
    }

    [Fact]
    public void TransferCancelled_round_trips_and_keeps_a_null_recommendation()
    {
        var sent = new TransferCancelled(412, "TR-2026-000412", RecommendationId: null, "Customer cancelled", At);

        var (received, json) = RoundTrip(sent);

        received.ShouldBeOfType<TransferCancelled>().ShouldBeEquivalentTo(sent);
        json.GetProperty("recommendationId").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("reason").GetString().ShouldBe("Customer cancelled");
    }

    [Fact]
    public void TransferRejected_carries_the_database_error_code()
    {
        var sent = new TransferRejected(77, "SS201", "Donor warehouse 3 would fall below its safety stock");

        var (received, json) = RoundTrip(sent);

        received.ShouldBeOfType<TransferRejected>().ShouldBeEquivalentTo(sent);
        PropertyNames(json).ShouldBe(["id", "occurredAt", "recommendationId", "errorCode", "message"], ignoreOrder: true);
    }

    [Fact]
    public void PartCatalogChanged_carries_part_snapshots_within_its_chunk_size()
    {
        var sent = new PartCatalogChanged([new PartSnapshot(1042, "JD", "RE900001", "Engine oil filter", "PCS", IsActive: true)]);

        var (received, json) = RoundTrip(sent);

        received.ShouldBeOfType<PartCatalogChanged>().Parts.ShouldHaveSingleItem().ShouldBe(sent.Parts[0]);
        PropertyNames(json.GetProperty("parts")[0]).ShouldBe(
            ["partId", "brandCode", "oemPartNumber", "description", "unitOfMeasure", "isActive"], ignoreOrder: true);
        PartCatalogChanged.MaxParts.ShouldBe(500, "40 messages for a 20,000-part import (ADR 0017 §5)");
    }

    [Fact]
    public void ServiceKitReserved_round_trips_with_its_lines_and_actor()
    {
        var sent = new ServiceKitReserved(9001, [new ReservedKitLine(71, 1), new ReservedKitLine(72, 12.5m)], FullyReserved: false, At.AddDays(7), Planner)
        { OccurredAt = At };

        var (received, json) = RoundTrip(sent);

        var reserved = received.ShouldBeOfType<ServiceKitReserved>();
        (reserved with { Lines = sent.Lines }).ShouldBe(sent);
        reserved.Lines.ShouldBe(sent.Lines);
        PropertyNames(json).ShouldBe(["id", "occurredAt", "serviceOrderId", "lines", "fullyReserved", "expiresAt", "actorId"], ignoreOrder: true);
        json.GetProperty("lines")[1].GetProperty("reservedQty").GetDecimal().ShouldBe(12.5m);
        reserved.ShouldBeAssignableTo<SmartSpare.SharedKernel.IActedByUser>()!.ActorId.ShouldBe(Planner);
    }

    [Fact]
    public void ServiceKitReservationExpired_names_the_lines_and_the_time()
    {
        var sent = new ServiceKitReservationExpired([71, 72], At) { OccurredAt = At };

        var (received, json) = RoundTrip(sent);

        var expired = received.ShouldBeOfType<ServiceKitReservationExpired>();
        expired.LineIds.ShouldBe([71L, 72L]);
        expired.ExpiredAt.ShouldBe(At);
        PropertyNames(json).ShouldBe(["id", "occurredAt", "lineIds", "expiredAt"], ignoreOrder: true);
        ServiceKitReservationExpired.MaxLines.ShouldBe(500);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void Outbox_type_names_are_namespace_type_and_assembly_without_a_version(string name)
    {
        var type = typeof(TransferApproved).Assembly.GetType($"SmartSpare.Modules.Inventory.Contracts.{name}")!;

        IntegrationEventSerializer.TypeName(type).ShouldBe($"SmartSpare.Modules.Inventory.Contracts.{name}, SmartSpare.Modules.Inventory.Contracts");
        IntegrationEventSerializer.ResolveType(IntegrationEventSerializer.TypeName(type)).ShouldBe(type);
    }

    private static (object Received, JsonElement Json) RoundTrip(SmartSpare.SharedKernel.IIntegrationEvent sent)
    {
        var message = IntegrationEventSerializer.ToOutboxMessage(sent);
        message.Id.ShouldBe(sent.Id);
        var received = IntegrationEventSerializer.Deserialize(message.Type, message.Payload);
        return (received, JsonDocument.Parse(message.Payload).RootElement.Clone());
    }

    private static List<string> PropertyNames(JsonElement json) => [.. json.EnumerateObject().Select(p => p.Name)];
}
