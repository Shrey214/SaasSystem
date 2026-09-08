using Microsoft.Extensions.Logging;

namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// Compile-time generated log methods.
//
// CA1848: LogWarning("...{X}", x) boxes every argument and formats the
// string even when the level is disabled. The [LoggerMessage] generator
// emits a cached delegate that does neither. It matters here because this
// is the error path - it runs when the system is already struggling.
internal static partial class ErrorLogMessages
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Error log buffer full; entry {ErrorId} dropped ({DroppedTotal} dropped so far)")]
    public static partial void BufferFull(ILogger logger, Guid errorId, int droppedTotal);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Failed to persist {Count} error log entries")]
    public static partial void PersistFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Dropped {Count} error log entries during shutdown")]
    public static partial void DroppedOnShutdown(ILogger logger, Exception exception, int count);
}
