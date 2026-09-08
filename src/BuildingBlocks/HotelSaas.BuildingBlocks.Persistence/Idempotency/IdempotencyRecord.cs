namespace HotelSaas.BuildingBlocks.Persistence.Idempotency;

// A remembered response to a state-changing request
// (docs/03-communication.md 4).
//
// The row is inserted BEFORE the work starts, so it doubles as a lock: a
// second request with the same key finds it in flight and gets a 409 rather
// than executing twice.
public sealed class IdempotencyRecord
{
    public long Id { get; set; }

    public required string Key { get; set; }

    // Guid.Empty means platform scope. Not nullable, because a unique index
    // over a nullable column does not deduplicate in postgres - nulls are
    // distinct from each other, so two platform requests with the same key
    // would both be allowed through.
    public Guid TenantId { get; set; }

    public required string Endpoint { get; set; }

    // Hash of the request body. Same key with a DIFFERENT body is a client
    // bug and gets a 422 - guessing which request was meant is how money
    // moves twice.
    public required string RequestHash { get; set; }

    public int? ResponseStatus { get; set; }

    public string? ResponseBody { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Null while the request is still executing.
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsCompleted => CompletedAt is not null;
}

// Proof that a consumer has already handled a message.
//
// Inserted in the same transaction as the side effect, so a duplicate
// insert means "we already did this" and the handler can return quietly.
// Keyed by consumer as well, because the same message is legitimately
// processed by six different services.
public sealed class ProcessedMessage
{
    public Guid MessageId { get; set; }

    public required string Consumer { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
