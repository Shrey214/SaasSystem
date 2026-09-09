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

// A domain event that must also reach other services.
//
// Not every domain event is publishable - most stay inside the service.
// Implementing this is what makes SaveChangesAsync copy it into the outbox
// in the same transaction (ADR-0006).
public interface IPublishableEvent : IDomainEvent
{
    // e.g. tenant.business.registered.v1
    string EventType { get; }

    string AggregateType { get; }

    Guid AggregateId { get; }

    // Declared by the event, not taken from the request context.
    //
    // Business registration is a PUBLIC endpoint - there is no tenant in
    // context yet, and the tenant the event refers to is the business being
    // created. Only the event knows that, so it says so explicitly rather
    // than the outbox guessing.
    Guid? TenantId { get; }
}
