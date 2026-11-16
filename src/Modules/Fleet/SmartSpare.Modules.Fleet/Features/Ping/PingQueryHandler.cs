using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Fleet.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Fleet.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(FleetModule.Name, "ok")));
}
