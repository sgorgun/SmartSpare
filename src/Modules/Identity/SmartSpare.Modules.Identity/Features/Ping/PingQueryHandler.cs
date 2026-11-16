using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(IdentityModule.Name, "ok")));
}
