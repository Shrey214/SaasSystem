using HotelSaas.BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.BuildingBlocks.Web;

// Turns an Error into an RFC 9457 problem response
// (docs/05-code-structure.md 7).
//
// The single place ErrorType becomes a status code. No service invents its
// own error shape, so a client can handle failures from all 14 identically.
public static class ProblemDetailsFactory
{
    private const string BaseTypeUri = "https://hotelsaas.dev/errors/";

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.RuleViolation => StatusCodes.Status422UnprocessableEntity,
        ErrorType.External => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static ProblemDetails Create(Error error, HttpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        ProblemDetails problem = new()
        {
            Type = BaseTypeUri + error.Code.Replace('.', '/'),
            Title = TitleFor(error.Type),
            Status = StatusCodeFor(error.Type),
            Detail = error.Message,
            Instance = context?.Request.Path.Value,
        };

        // The machine-readable code, alongside the human-readable detail.
        // Clients switch on this, never on the message text.
        problem.Extensions["code"] = error.Code;

        AddDiagnostics(problem, context);
        return problem;
    }

    // 500 responses deliberately carry no detail about what went wrong -
    // only the ids needed to find the error_logs row. A stack trace in a
    // response body is a gift to an attacker.
    public static ProblemDetails CreateInternal(Guid errorId, HttpContext? context)
    {
        ProblemDetails problem = new()
        {
            Type = BaseTypeUri + "internal",
            Title = "An unexpected error occurred.",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The request could not be completed. Quote the error id if you contact support.",
            Instance = context?.Request.Path.Value,
        };

        problem.Extensions["code"] = "internal_error";
        problem.Extensions["errorId"] = errorId;

        AddDiagnostics(problem, context);
        return problem;
    }

    public static ProblemDetails CreateValidation(
        IReadOnlyDictionary<string, string[]> errors,
        HttpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(errors);

        ProblemDetails problem = new()
        {
            Type = BaseTypeUri + "validation_failed",
            Title = "One or more fields are invalid.",
            Status = StatusCodes.Status400BadRequest,
            Instance = context?.Request.Path.Value,
        };

        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["errors"] = errors;

        AddDiagnostics(problem, context);
        return problem;
    }

    private static void AddDiagnostics(ProblemDetails problem, HttpContext? context)
    {
        if (context is null)
        {
            return;
        }

        Guid? correlationId = Middleware.CorrelationIdMiddleware.GetCorrelationId(context);
        if (correlationId is not null)
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        string? traceId = System.Diagnostics.Activity.Current?.Id;
        if (traceId is not null)
        {
            problem.Extensions["traceId"] = traceId;
        }
    }

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "Invalid request.",
        ErrorType.Unauthorized => "Authentication required.",
        ErrorType.Forbidden => "Not permitted.",
        ErrorType.NotFound => "Not found.",
        ErrorType.Conflict => "Conflict.",
        ErrorType.RuleViolation => "Request refused by a business rule.",
        ErrorType.External => "An upstream service failed.",
        _ => "Error.",
    };
}
