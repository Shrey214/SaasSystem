namespace HotelSaas.BuildingBlocks.Domain;

// Base class for anything with an identity and a lifetime.
public abstract class Entity : IAuditable, IEquatable<Entity>
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An entity id cannot be empty.", nameof(id));
        }

        Id = id;
    }

    // Materialisation constructor, for EF Core only.
    protected Entity()
    {
    }

    public Guid Id { get; protected set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Identity equality, not structural: two rows with the same id are the
    // same thing even if one of them is stale.
    public bool Equals(Entity? other)
        => other is not null
           && GetType() == other.GetType()
           && Id != Guid.Empty
           && Id == other.Id;

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}
