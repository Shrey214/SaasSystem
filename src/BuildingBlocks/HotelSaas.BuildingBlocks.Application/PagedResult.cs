namespace HotelSaas.BuildingBlocks.Application;

// A page of results, cursor-based.
//
// Cursors rather than offsets: an offset drifts under concurrent inserts,
// so page 2 can repeat or skip rows page 1 already returned
// (docs/00-conventions.md 5).
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}

// Companion for the factory. A static member on the generic type itself
// trips CA1000, and the rule has a point: PagedResult<Booking>.Empty and
// PagedResult<Room>.Empty read as if they were the same member.
public static class PagedResult
{
    public static PagedResult<T> Empty<T>() => new([], null);
}
