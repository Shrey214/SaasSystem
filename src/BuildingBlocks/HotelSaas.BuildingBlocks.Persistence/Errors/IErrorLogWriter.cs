namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// Records a fault. Must never throw and must never block the caller.
//
// An error logger that fails during error handling takes down the response
// - a 500 inside a 500, with the original cause lost.
public interface IErrorLogWriter
{
    // Hands the entry to a background writer and returns immediately.
    // False means the buffer was full and the entry was dropped, which is a
    // deliberate outcome under an error storm, not a failure to report.
    bool Enqueue(ErrorLogEntry entry);
}

public sealed class ErrorLogWriterOptions
{
    public required string ConnectionString { get; set; }

    public required string ServiceName { get; set; }

    public string Environment { get; set; } = "Unknown";

    public string? Version { get; set; }

    // Bounded on purpose. Unbounded means an error storm becomes an
    // out-of-memory crash on top of whatever was already wrong.
    public int Capacity { get; set; } = 1024;

    public int BatchSize { get; set; } = 50;

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    // Short. A sick database is often WHY we are logging an error, and a
    // long timeout here would stall the writer instead of the request.
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
