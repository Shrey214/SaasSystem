namespace HotelSaas.BuildingBlocks.Application;

// Which tenant the current request belongs to.
//
// ADR-0004 layer 1: this value comes from validated token claims and from
// nowhere else. There is deliberately no way to ask for a different tenant
// - no parameter, no setter - because a handler that can choose its own
// tenant is one bug away from a cross-tenant leak.
public interface ITenantContext
{
    // Null means platform scope, i.e. no tenant filter applies. That is why
    // platform access takes an explicit, audited code path rather than a
    // nullable filter: "where tenant_id = @t or @t is null" is one typo
    // away from returning everything.
    Guid? TenantId { get; }

    bool IsPlatformScope => TenantId is null;

    Guid RequireTenantId() => TenantId
        ?? throw new InvalidOperationException(
            "This operation requires a tenant, but the request is platform-scoped.");
}

// The authenticated principal behind the current request.
public interface ICurrentUser
{
    Guid? UserId { get; }

    bool IsAuthenticated => UserId is not null;

    // Property ids this user may act on (docs/01-bounded-contexts.md 2.8).
    IReadOnlySet<Guid> AuthorizedPropertyIds { get; }

    bool CanAccessProperty(Guid propertyId) => AuthorizedPropertyIds.Contains(propertyId);
}

// The current time, as a dependency.
//
// Nothing calls DateTimeOffset.UtcNow directly. Hold expiry, cancellation
// windows and rate seasons are all time-dependent business rules, and a
// rule that reads the clock statically cannot be tested without waiting.
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    // A stay night is a date, not an instant (docs/00-conventions.md 3),
    // and "today" at a property in Ujjain is not "today" in UTC at 23:00.
    DateOnly Today(TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).Date);
}

// Commits the current change set.
//
// Exists so a handler in Application can commit without referencing EF
// Core, which lives in Infrastructure. Saving also drains domain events
// into the outbox in the same transaction (ADR-0006).
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

// The id that ties this request to everything it causes.
//
// Minted at the edge and copied into every downstream call and every
// message (docs/03-communication.md 5). One value spans a whole customer
// booking across nine services, which is what makes "why did this booking
// not confirm" answerable at all.
public interface ICorrelationContext
{
    Guid CorrelationId { get; }
}
