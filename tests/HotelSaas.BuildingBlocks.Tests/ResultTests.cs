using HotelSaas.BuildingBlocks.Application;

namespace HotelSaas.BuildingBlocks.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Success_CarriesTheValue()
    {
        Result<int> result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_CarriesTheError()
    {
        Error error = Error.Conflict("booking.no_capacity", "No rooms remain.");

        Result<int> result = Result.Failure<int>(error);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void ReadingTheValueOfAFailure_Throws()
    {
        Result<int> result = Result.Failure<int>(Error.NotFound("x.missing", "Gone."));

        // Reading the value without checking is a programming error, and it
        // should be loud rather than silently returning default.
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void AnErrorConvertsImplicitlyToAFailedResult()
    {
        // Lets a handler write `return BookingErrors.NoCapacity;`
        Result<string> result = Error.Conflict("booking.no_capacity", "No rooms remain.");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("booking.no_capacity");
    }

    [Fact]
    public void AValueConvertsImplicitlyToASuccessfulResult()
    {
        Result<string> result = "done";

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("done");
    }

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.RuleViolation, 422)]
    [InlineData(ErrorType.External, 502)]
    public void EveryErrorTypeMapsToItsStatusCode(ErrorType type, int expected)
        => Web.ProblemDetailsFactory.StatusCodeFor(type).ShouldBe(expected);
}
