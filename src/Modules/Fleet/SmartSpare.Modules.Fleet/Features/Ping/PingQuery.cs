using SmartSpare.Infrastructure.Messaging;

namespace SmartSpare.Modules.Fleet.Features.Ping;

internal sealed record PingQuery : IQuery<PingResponse>;

internal sealed record PingResponse(string Module, string Status);
