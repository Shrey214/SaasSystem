namespace HotelSaas.BuildingBlocks.Messaging;

// The wrapper every integration event travels in (docs/00-conventions.md 6).
// Identical across all 14 services.
public sealed record EventEnvelope
{
    // The consumer idempotency key. Delivery is at-least-once, so every
    // consumer must assume it will see the same message twice (ADR-0006).
    public required Guid MessageId { get; init; }

    // e.g. booking.booking.confirmed.v1
    public required string EventType { get; init; }

    // The originating request, propagated unchanged across every hop. One
    // value spans a whole customer booking across nine services.
    public required Guid CorrelationId { get; init; }

    // The message that directly caused this one, giving a parent chain
    // rather than a flat set.
    public Guid? CausationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public Guid? TenantId { get; init; }

    // The event body, already serialised.
    public required string Payload { get; init; }
}

// Builds and reads event type names.
//
// Format: <service>.<aggregate>.<past-tense-verb>.v<n>. A new version is a
// new name - existing events are never reshaped, because a consumer we have
// forgotten about is still reading the old one.
public static class IntegrationEventNames
{
    public static string Build(string service, string aggregate, string verb, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        return $"{service}.{aggregate}.{verb}.v{version}";
    }

    // The publishing service, used to route to its exchange.
    public static string ServiceOf(string eventType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        int firstDot = eventType.IndexOf('.', StringComparison.Ordinal);
        return firstDot > 0
            ? eventType[..firstDot]
            : throw new ArgumentException($"Malformed event type: {eventType}", nameof(eventType));
    }
}
