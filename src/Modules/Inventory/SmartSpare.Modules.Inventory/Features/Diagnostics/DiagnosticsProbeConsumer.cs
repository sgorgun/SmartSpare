using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Modules.Inventory.Features.Diagnostics;

/// <summary>
/// Receives Identity's diagnostics probe (STAGE_4_PLAN 5.3, 6.5): its inbox row and its log record prove that an event
/// crossed from one module to another, and in the services topology across the broker. Removed with #55.
/// </summary>
[IntegrationEventConsumer(Name)]
internal sealed partial class DiagnosticsProbeConsumer(ILogger<DiagnosticsProbeConsumer> logger) : IIntegrationEventConsumer<DiagnosticsProbeSent>
{
    public const string Name = "inventory.diagnostics-probe";

    public Task Handle(DiagnosticsProbeSent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        LogReceived(integrationEvent.ProbeId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Diagnostics probe {ProbeId} received from Identity.")]
    private partial void LogReceived(Guid probeId);
}
