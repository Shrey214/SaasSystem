using System.Diagnostics;
using System.Text.Json;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Persistence.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelSaas.BuildingBlocks.Web.Middleware;

// Catches everything that reaches the top of the pipeline (ADR-0008).
//
// Anything arriving here is a bug or an outage: an expected failure would
// have travelled as a Result. So it is recorded once, in one place, and the
// caller gets a safe response with an id they can quote.
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IErrorLogWriter errorLogWriter,
    IHostEnvironment environment)
{
    // Everything else is redacted. Headers routinely carry tokens, cookies
    // and api keys, and an error table is exactly the wrong place for them.
    private static readonly string[] SafeHeaders =
    [
        "User-Agent", "Referer", "Accept", "Accept-Language",
        "Content-Type", "X-Correlation-Id", "Idempotency-Key",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a global handler must catch everything, by definition
        catch (Exception exception)
#pragma warning restore CA1031
        {
            await HandleAsync(context, exception, startedAt).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception, long startedAt)
    {
        // Malformed JSON, a missing required parameter, an unparseable route
        // value: the framework raises these as exceptions, but they are the
        // CALLER's mistake, not ours.
        //
        // Treating them as 500 is wrong twice over: the client is told to
        // contact support about their own typo, and error_logs fills up with
        // other people's bad requests, burying the real faults. This was
        // observed for real - a missing ?limit= returned 500.
        if (exception is BadHttpRequestException badRequest)
        {
            await HandleBadRequestAsync(context, badRequest).ConfigureAwait(false);
            return;
        }

        Guid errorId = Uuid7.New();
        Guid? correlationId = CorrelationIdMiddleware.GetCorrelationId(context);

        // 1. stdout first, and unconditionally. This is the authoritative
        //    record and it works even when the database is the problem.
        WebLogMessages.UnhandledException(
            logger,
            exception,
            errorId,
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            correlationId);

        // 2. buffer for the background writer. Enqueue never throws and
        //    never blocks - an error logger that fails during error handling
        //    would take down the response it exists to describe.
        errorLogWriter.Enqueue(BuildEntry(context, exception, errorId, correlationId, startedAt));

        // 3. respond. If the response has already begun, headers and status
        //    are locked and there is nothing valid left to write.
        if (context.Response.HasStarted)
        {
            WebLogMessages.ResponseAlreadyStarted(logger, errorId);
            return;
        }

        ProblemDetails problem = ProblemDetailsFactory.CreateInternal(errorId, context);

        // Development only: the exception detail goes in the response so it
        // is visible without opening the database. Never outside development.
        if (environment.IsDevelopment())
        {
            problem.Extensions["exception"] = exception.GetType().FullName;
            problem.Extensions["exceptionMessage"] = exception.Message;
            problem.Extensions["stackTrace"] = exception.StackTrace;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await context.Response
            .WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json")
            .ConfigureAwait(false);
    }

    private async Task HandleBadRequestAsync(HttpContext context, BadHttpRequestException exception)
    {
        // Logged at Warning, not Error, and never written to error_logs.
        WebLogMessages.BadRequest(logger, context.Request.Method, context.Request.Path.Value ?? "/", exception.Message);

        if (context.Response.HasStarted)
        {
            return;
        }

        Error error = Error.Validation(
            "malformed_request",

            // The framework's message names the parameter or the JSON path,
            // which is genuinely useful to a developer and reveals nothing.
            environment.IsDevelopment()
                ? exception.Message
                : "The request could not be read. Check the body and the query string.");

        ProblemDetails problem = ProblemDetailsFactory.Create(error, context);

        context.Response.Clear();
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";

        await context.Response
            .WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json")
            .ConfigureAwait(false);
    }

    private ErrorLogEntry BuildEntry(
        HttpContext context,
        Exception exception,
        Guid errorId,
        Guid? correlationId,
        long startedAt)
    {
        FaultLocator.FaultLocation? fault = FaultLocator.Locate(exception);

        return new ErrorLogEntry
        {
            ErrorId = errorId,
            OccurredAt = DateTimeOffset.UtcNow,
            ServiceName = environment.ApplicationName,
            Environment = environment.EnvironmentName,
            MachineName = System.Environment.MachineName,
            Version = typeof(ExceptionHandlingMiddleware).Assembly.GetName().Version?.ToString(),
            CorrelationId = correlationId,
            TenantId = TenantContextMiddleware.GetTenantId(context),
            UserId = ReadUserId(context),
            SourceKind = "http",
            HttpMethod = context.Request.Method,
            Path = context.Request.Path.Value,
            QueryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            StatusCode = StatusCodes.Status500InternalServerError,
            DurationMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace,
            InnerExceptions = FaultLocator.DescribeInnerExceptions(exception),
            FaultAssembly = fault?.Assembly,
            FaultType = fault?.Type,
            FaultMethod = fault?.Method,
            FaultFile = fault?.File,
            FaultLine = fault?.Line,
            RequestHeaders = SerialiseSafeHeaders(context),
            Fingerprint = FaultLocator.Fingerprint(exception, fault),
        };
    }

    private static Guid? ReadUserId(HttpContext context)
    {
        string? raw = context.User?.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out Guid userId) ? userId : null;
    }

    private static string? SerialiseSafeHeaders(HttpContext context)
    {
        Dictionary<string, string> headers = [];

        foreach (string name in SafeHeaders)
        {
            string? value = context.Request.Headers[name].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                headers[name] = value;
            }
        }

        return headers.Count == 0 ? null : JsonSerializer.Serialize(headers);
    }
}
