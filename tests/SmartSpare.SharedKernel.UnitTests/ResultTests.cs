namespace SmartSpare.SharedKernel.UnitTests;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.Conflict("SS200", "Insufficient stock");

    [Fact]
    public void Success_HasNoError()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_CarriesError()
    {
        var result = Result.Failure(SampleError);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(SampleError);
    }

    [Fact]
    public void Failure_WithNoneError_Throws() =>
        Should.Throw<ArgumentException>(() => Result.Failure(Error.None));

    [Fact]
    public void GenericSuccess_ExposesValue()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void GenericFailure_AccessingValue_Throws()
    {
        var result = Result.Failure<int>(SampleError);

        result.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromValue_IsSuccess()
    {
        Result<string> result = "ok";

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("ok");
    }

    [Fact]
    public void ImplicitConversion_FromError_IsFailure()
    {
        Result<string> result = SampleError;
        Result plain = SampleError;

        result.Error.ShouldBe(SampleError);
        plain.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ErrorFactories_SetType()
    {
        Error.Validation("c", "d").Type.ShouldBe(ErrorType.Validation);
        Error.NotFound("c", "d").Type.ShouldBe(ErrorType.NotFound);
        Error.Forbidden("c", "d").Type.ShouldBe(ErrorType.Forbidden);
        Error.BusinessRule("c", "d").Type.ShouldBe(ErrorType.BusinessRule);
        Error.Failure("c", "d").Type.ShouldBe(ErrorType.Failure);
    }
}
