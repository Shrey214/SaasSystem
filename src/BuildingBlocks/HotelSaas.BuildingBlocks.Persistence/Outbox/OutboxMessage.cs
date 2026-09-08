namespace HotelSaas.BuildingBlocks.Persistence.Outbox;

// A message waiting to be published (ADR-0006).
//
// Written in the SAME transaction as the business change, so the two commit
// or fail together. Publishing directly from a handler has two failure
// modes and both happen: publish-then-rollback announces something that
// never happened, and write-then-fail-to-publish means the world never
// hears about something that did.
public sealed class OutboxMessage
{
    // bigint identity, not uuid (ADR-0010). Never exposed, never referenced
    // from another service, highest volume table in the system - and the
    // monotonic sequence gives ordered draining without depending on a
    // timestamp with clock skew and equal-millisecond ties.
    public long Id { get; set; }

    // The consumer idempotency key. Globally unique, so it stays a uuid.
    public Guid MessageId { get; set; }

    public Guid? TenantId { get; set; }

    public required string AggregateType { get; set; }

    public Guid AggregateId { get; set; }

    public required string EventType { get; set; }

    public required string Payload { get; set; }

    public Guid CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    // Null until the broker has acknowledged it.
    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
