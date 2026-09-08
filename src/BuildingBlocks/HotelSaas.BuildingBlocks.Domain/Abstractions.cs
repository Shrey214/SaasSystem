namespace HotelSaas.BuildingBlocks.Domain;

// Marks an entity as belonging to exactly one tenant.
//
// ADR-0004 layer 2: implementing this is what gets an entity filtered and
// stamped automatically. A new entity is protected because of the interface
// it implements, not because someone remembered to add a Where clause.
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

// Timestamps every table carries (docs/00-conventions.md 3).
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}

// Something that happened inside the domain, raised by an aggregate.
//
// Not the same as an integration event: a domain event stays inside the
// service, and the outbox turns selected ones into integration events for
// other services (ADR-0006).
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}
