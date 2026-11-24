using Microsoft.Extensions.Localization;
using SmartSpare.ApiContracts;
using SmartSpare.Web.Client.Resources;

namespace SmartSpare.Web.Client.Http;

/// <summary>
/// Turns a ProblemDetails into the message a user reads (STAGE_3_PLAN 8.5): a localized text for the known codes, the
/// server's detail for business errors, and always the trace id as "Reference: …", so support can find the request
/// in the traces and logs (#60, #72).
/// </summary>
public sealed class ProblemMessages(IStringLocalizer<SharedResource> text)
{
    public string Describe(ProblemInfo problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var message = problem.Code switch
        {
            ProblemCodes.InvalidCredentials => text["Login.Failed"].Value,
            ProblemCodes.AntiforgeryFailed => text["Error.Antiforgery"].Value,
            ProblemCodes.Unauthenticated => text["Error.Unauthenticated"].Value,
            ProblemCodes.Forbidden => text["Error.Forbidden"].Value,
            ProblemCodes.TooManyRequests => text["Error.TooManyRequests"].Value,
            _ when problem.Status == 403 => text["Error.Forbidden"].Value,
            _ when problem.Status is >= 400 and < 500 && !string.IsNullOrWhiteSpace(problem.Detail) => problem.Detail,
            _ => text["Error.Generic"].Value,
        };

        return string.IsNullOrWhiteSpace(problem.TraceId)
            ? message
            : $"{message} {text["Error.Reference", problem.TraceId].Value}";
    }
}
