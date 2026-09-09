using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Tenant.Domain.Businesses;

// Events this service publishes (docs/03-communication.md 6).
//
// TenantId is the business id itself: this service owns the tenant root, so
// the business being created IS the tenant the event refers to.
//
// Each carries the FACTS a consumer needs, not just an id, so subscription
// and reporting never have to call back synchronously.

public sealed record BusinessRegistered(
    Guid BusinessId,
    string LegalName,
    string OwnerEmail,
    string Country,
    DateTimeOffset RegisteredAt) : IPublishableEvent
{
    public Guid EventId { get; } = Uuid7.New();

    public DateTimeOffset OccurredAt => RegisteredAt;

    public string EventType => "tenant.business.registered.v1";

    public string AggregateType => "business";

    public Guid AggregateId => BusinessId;

    public Guid? TenantId => BusinessId;
}

public sealed record BusinessActivated(
    Guid BusinessId,
    string LegalName,
    DateTimeOffset ActivatedAt) : IPublishableEvent
{
    public Guid EventId { get; } = Uuid7.New();

    public DateTimeOffset OccurredAt => ActivatedAt;

    public string EventType => "tenant.business.activated.v1";

    public string AggregateType => "business";

    public Guid AggregateId => BusinessId;

    public Guid? TenantId => BusinessId;
}

public sealed record BusinessSuspended(
    Guid BusinessId,
    string Reason,
    DateTimeOffset SuspendedAt) : IPublishableEvent
{
    public Guid EventId { get; } = Uuid7.New();

    public DateTimeOffset OccurredAt => SuspendedAt;

    public string EventType => "tenant.business.suspended.v1";

    public string AggregateType => "business";

    public Guid AggregateId => BusinessId;

    public Guid? TenantId => BusinessId;
}

public sealed record BusinessArchived(
    Guid BusinessId,
    DateTimeOffset ArchivedAt) : IPublishableEvent
{
    public Guid EventId { get; } = Uuid7.New();

    public DateTimeOffset OccurredAt => ArchivedAt;

    public string EventType => "tenant.business.archived.v1";

    public string AggregateType => "business";

    public Guid AggregateId => BusinessId;

    public Guid? TenantId => BusinessId;
}
