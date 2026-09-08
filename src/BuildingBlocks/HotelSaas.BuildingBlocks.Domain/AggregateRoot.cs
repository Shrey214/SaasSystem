namespace HotelSaas.BuildingBlocks.Domain;

// The only kind of entity a repository may load or save.
//
// An aggregate root is the consistency boundary: everything inside it is
// saved in one transaction and its invariants hold at every commit.
// Commands go through these; queries do not (docs/05-code-structure.md 2.5).
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    // Optimistic concurrency token (docs/00-conventions.md 3).
    public int Version { get; set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    // Called once the events have been written to the outbox.
    public void ClearDomainEvents() => _domainEvents.Clear();
}
