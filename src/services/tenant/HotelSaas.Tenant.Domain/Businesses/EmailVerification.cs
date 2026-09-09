using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Tenant.Domain.Businesses;

// A single-use email verification token.
//
// Only the HASH is stored. A database dump of plain tokens would be a set
// of working account-takeover links, and this table is exactly the kind of
// thing that ends up in a support export.
public sealed class EmailVerification : Entity
{
    public const int ValidForHours = 24;

    private EmailVerification(Guid id, Guid businessId, string tokenHash, DateTimeOffset expiresAt)
        : base(id)
    {
        BusinessId = businessId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    private EmailVerification()
    {
    }

    public Guid BusinessId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    // Superseded by a resend. Kept rather than deleted so "how many links
    // did this person request" is answerable.
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public bool IsConsumed => ConsumedAt is not null;

    public bool IsInvalidated => InvalidatedAt is not null;

    public static EmailVerification Issue(Guid businessId, string tokenHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        return new EmailVerification(
            Uuid7.New(),
            businessId,
            tokenHash,
            now.AddHours(ValidForHours));
    }

    public bool IsUsable(DateTimeOffset now) => !IsConsumed && !IsInvalidated && now < ExpiresAt;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void Consume(DateTimeOffset now) => ConsumedAt = now;

    public void Invalidate(DateTimeOffset now) => InvalidatedAt = now;
}

// Append-only record of every lifecycle change.
//
// Goal/Domain.txt Part 18 wants who / what / when / old / new. A status
// column alone answers "what is it now" and never "who suspended this
// customer, and why".
public sealed class BusinessStatusChange : Entity
{
    private BusinessStatusChange(
        Guid id,
        Guid businessId,
        BusinessStatus? fromStatus,
        BusinessStatus toStatus,
        string reason,
        Guid? changedByUserId)
        : base(id)
    {
        BusinessId = businessId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        ChangedByUserId = changedByUserId;
    }

    private BusinessStatusChange()
    {
    }

    public Guid BusinessId { get; private set; }

    // Null for the very first entry - there was no previous status.
    public BusinessStatus? FromStatus { get; private set; }

    public BusinessStatus ToStatus { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    // Null when the system did it, e.g. the owner verifying their own email.
    public Guid? ChangedByUserId { get; private set; }

    public static BusinessStatusChange Record(
        Guid businessId,
        BusinessStatus? from,
        BusinessStatus to,
        string reason,
        Guid? changedByUserId)
        => new(Uuid7.New(), businessId, from, to, reason, changedByUserId);
}
