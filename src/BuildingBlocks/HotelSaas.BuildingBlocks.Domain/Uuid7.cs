namespace HotelSaas.BuildingBlocks.Domain;

// Time-ordered identifiers for entity primary keys. See ADR-0010.
//
// v7 (RFC 9562) prefixes a millisecond timestamp, so values sort by
// creation time and insert at the right-hand edge of a B-tree instead of
// fragmenting it the way v4 does. Generated here rather than by the
// database because an aggregate must know its own id before the row
// exists - a domain event raised in the constructor already carries it.
public static class Uuid7
{
    public static Guid New() => Guid.CreateVersion7();

    // For tests that need a deterministic point in time.
    public static Guid New(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
