using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SmartSpare.Infrastructure.Http;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.UnitTests;

public sealed class ResultHttpExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorType.BusinessRule, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError)]
    public void ErrorType_MapsToStatusCode(ErrorType type, int expected)
    {
        var problem = new Error("SS200", "Insufficient stock", type).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(expected);
        problem.ProblemDetails.Extensions["code"].ShouldBe("SS200");
    }

    [Fact]
    public void ValidationError_BecomesValidationProblem()
    {
        var error = new ValidationError(new Dictionary<string, string[]> { ["Name"] = ["Required"] });

        var problem = error.ToProblem().ShouldBeOfType<ValidationProblem>();

        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Errors["Name"].ShouldBe(["Required"]);
    }

    [Fact]
    public void Success_BecomesOk() =>
        Result.Success(5).ToHttpResult().ShouldBeOfType<Ok<int>>().Value.ShouldBe(5);
}
