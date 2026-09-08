namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// One recorded fault (ADR-0008). Lives in each service own database.
public sealed class ErrorLogEntry
{
    public long Id { get; set; }

    // Shown to the user in the problem response, so support can find the
    // row from a screenshot.
    public Guid ErrorId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string ServiceName { get; set; }

    public required string Environment { get; set; }

    public required string MachineName { get; set; }

    public string? Version { get; set; }

    // Ties this row to the same request in the other 13 services.
    public Guid? CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    // http | consumer | job. Most failures in an event-driven system happen
    // outside an HTTP request, and without this they would be invisible.
    public required string SourceKind { get; set; }

    public string? HttpMethod { get; set; }

    public string? Path { get; set; }

    public string? QueryString { get; set; }

    public int? StatusCode { get; set; }

    public int? DurationMs { get; set; }

    public string? MessageType { get; set; }

    public required string ExceptionType { get; set; }

    public required string Message { get; set; }

    public string? StackTrace { get; set; }

    public string? InnerExceptions { get; set; }

    // Where it broke in OUR code. The top stack frame is usually framework
    // internals and tells you nothing.
    public string? FaultAssembly { get; set; }

    public string? FaultType { get; set; }

    public string? FaultMethod { get; set; }

    public string? FaultFile { get; set; }

    public int? FaultLine { get; set; }

    public string? RequestHeaders { get; set; }

    // Hash of exception type + fault location, so one bug that fired 4,000
    // times is one row to read instead of 4,000.
    public required string Fingerprint { get; set; }
}
