using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Reporting.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Reporting.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(ReportingModule.Name, "ok")));
}
