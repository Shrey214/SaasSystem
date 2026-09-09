using Microsoft.Extensions.Logging;

namespace HotelSaas.BuildingBlocks.Web.Middleware;

internal static partial class WebLogMessages
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Unhandled exception {ErrorId} on {Method} {Path} (correlation {CorrelationId})")]
    public static partial void UnhandledException(
        ILogger logger,
        Exception exception,
        Guid errorId,
        string method,
        string path,
        Guid? correlationId);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Malformed request on {Method} {Path}: {Reason}")]
    public static partial void BadRequest(ILogger logger, string method, string path, string reason);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Exception occurred after the response had started; cannot write a problem response for {ErrorId}")]
    public static partial void ResponseAlreadyStarted(ILogger logger, Guid errorId);
}
