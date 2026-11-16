using Microsoft.AspNetCore.Http;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Http;

/// <summary>Maps <see cref="Result"/> and <see cref="Error"/> to HTTP results (RFC 9457 problem details).</summary>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        return error is ValidationError validation
            ? TypedResults.ValidationProblem(validation.Errors, error.Description, title: "Validation failed", extensions: extensions)
            : TypedResults.Problem(
                detail: error.Description,
                statusCode: StatusCodeFor(error.Type),
                title: TitleFor(error.Type),
                extensions: extensions);
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.NotFound => "Resource not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.Forbidden => "Operation not permitted",
        ErrorType.BusinessRule => "Business rule violated",
        _ => "Unexpected error",
    };
}
