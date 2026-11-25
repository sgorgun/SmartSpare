using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// A diagnostics event that proves the integration-event path end to end before the first business events exist
/// (STAGE_4_PLAN 5.3, 6.5): Identity publishes it, Inventory consumes it, and one trace follows it across the broker.
/// Removed together with the other diagnostics and stub endpoints before the defence (#55).
/// </summary>
public sealed record DiagnosticsProbeSent(Guid ProbeId) : IntegrationEvent;
