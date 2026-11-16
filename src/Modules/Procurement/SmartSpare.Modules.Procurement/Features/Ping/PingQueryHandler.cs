using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Procurement.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Procurement.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(ProcurementModule.Name, "ok")));
}
