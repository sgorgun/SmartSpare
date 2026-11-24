using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>Raised when a user is deactivated; their sessions end at the next session check (ADR 0016).</summary>
internal sealed record UserDeactivated(Guid UserId, DateTimeOffset OccurredAt) : IDomainEvent;
