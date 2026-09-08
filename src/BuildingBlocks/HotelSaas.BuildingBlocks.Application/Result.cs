namespace HotelSaas.BuildingBlocks.Application;

// The outcome of a use case: success, or an Error.
//
// Exceptions are for the unexpected. "No capacity for those dates" is the
// normal outcome of a race we lost (ADR-0002), so it travels as a value.
// Using exceptions for expected outcomes makes the normal path expensive
// and buries real faults in the noise.
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new InvalidOperationException("A successful result cannot carry an error.");
        }

        if (!isSuccess && error == Error.None)
        {
            throw new InvalidOperationException("A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

// A result that carries a value when it succeeds.
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error)
        => _value = value;

    // Throws if the result is a failure. Check IsSuccess first.
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}

// A page of results, cursor-based.
//
// Cursors rather than offsets: an offset drifts under concurrent inserts,
// so page 2 can repeat or skip rows page 1 already returned
// (docs/00-conventions.md 5).
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}

// Companion for the factory. A static member on the generic type itself
// trips CA1000, and the rule has a point: PagedResult<Booking>.Empty and
// PagedResult<Room>.Empty read as if they were the same member.
public static class PagedResult
{
    public static PagedResult<T> Empty<T>() => new([], null);
}
