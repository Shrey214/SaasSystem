namespace HotelSaas.BuildingBlocks.Application;

// What kind of failure this is. Mapped to an HTTP status code in exactly
// one place (docs/05-code-structure.md 7).
public enum ErrorType
{
    // Malformed input. 400.
    Validation = 0,

    // Not authenticated. 401.
    Unauthorized = 1,

    // Authenticated but not permitted. 403.
    Forbidden = 2,

    // Absent, or belongs to another tenant. 404 - never 403, which would
    // confirm the row exists and leak across tenants.
    NotFound = 3,

    // State clash: no capacity, duplicate email. 409. A normal outcome.
    Conflict = 4,

    // Understood, but a business rule refuses. 422.
    RuleViolation = 5,

    // A gateway or upstream service failed. 502.
    External = 6,
}

// An expected failure, carried by Result rather than thrown.
//
// Code is stable, machine-readable and dot-separated - booking.no_capacity.
// The frontend switches on it and translates it, so it is part of the API
// contract and does not change once shipped.
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Validation);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error RuleViolation(string code, string message) => new(code, message, ErrorType.RuleViolation);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error External(string code, string message) => new(code, message, ErrorType.External);
}
