using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Identity.Domain.Access;

// One live session on one device.
//
// This table is what makes revocation actually work. Access tokens are
// validated locally by every service with no call back here (ADR-0005), so
// nothing can un-issue one - the only lever is refusing to mint the next
// one. Deleting a row here ends the session at its next refresh, which is
// at most one access-token lifetime away.
public sealed class RefreshToken : Entity
{
    private RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        string? device,
        string? ipAddress,
        DateTimeOffset expiresAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        Device = device;
        IpAddress = ipAddress;
        ExpiresAt = expiresAt;
    }

    private RefreshToken()
    {
    }

    public Guid UserId { get; private set; }

    // SHA-256 hex of 32 random bytes. Same reasoning as every other token
    // in the system: a database dump must not be a set of live sessions.
    public string TokenHash { get; private set; } = string.Empty;

    // Best-effort labels so a user can recognise their own sessions in a
    // "sign out everywhere" screen. Never used for a security decision -
    // both are trivially spoofed by the caller.
    public string? Device { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    // Set when this token was ROTATED. Together these form a chain, which
    // is what makes replay detectable: a token that was already replaced
    // being presented again means somebody has a copy they should not.
    public Guid? ReplacedByTokenId { get; private set; }

    public string? RevokedReason { get; private set; }

    public bool IsActive => RevokedAt is null && ReplacedByTokenId is null;

    public static RefreshToken Issue(
        Guid userId,
        string tokenHash,
        string? device,
        string? ipAddress,
        DateTimeOffset now,
        TimeSpan lifetime)
        => new(Uuid7.New(), userId, tokenHash, device, ipAddress, now.Add(lifetime));

    public bool IsUsable(DateTimeOffset now) => IsActive && now < ExpiresAt;

    // Rotation: this token is spent and points at its successor.
    public void ReplaceWith(Guid successorId, DateTimeOffset now)
    {
        ReplacedByTokenId = successorId;
        RevokedAt = now;
        RevokedReason = "rotated";
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }
}

// An RS256 key pair used to sign access tokens.
//
// A table rather than a config value because rotation needs TWO keys live
// at once: tokens signed with the outgoing key must keep validating until
// the last one expires. The `kid` header on a token says which one to use,
// and JWKS publishes every key still accepting verification.
public sealed class SigningKey : Entity
{
    private SigningKey(Guid id, string keyId, string publicKeyPem, string privateKeyPem, DateTimeOffset now)
        : base(id)
    {
        KeyId = keyId;
        PublicKeyPem = publicKeyPem;
        PrivateKeyPem = privateKeyPem;
        ActivatedAt = now;
    }

    private SigningKey()
    {
    }

    // The `kid` claim in the JWT header.
    public string KeyId { get; private set; } = string.Empty;

    // Published at /.well-known/jwks.json. Public by design.
    public string PublicKeyPem { get; private set; } = string.Empty;

    // NEVER leaves this service. Stored encrypted at rest from stage 20;
    // stage 5 keeps it in the row and says so out loud rather than
    // pretending otherwise.
    public string PrivateKeyPem { get; private set; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; private set; }

    // Stops being used for SIGNING, keeps being published for VERIFYING.
    public DateTimeOffset? RetiredAt { get; private set; }

    // No longer published. Any token it signed is long expired.
    public DateTimeOffset? ExpiredAt { get; private set; }

    public bool IsSigningKey => RetiredAt is null;

    public bool IsVerificationKey => ExpiredAt is null;

    public static SigningKey Create(string keyId, string publicKeyPem, string privateKeyPem, DateTimeOffset now)
        => new(Uuid7.New(), keyId, publicKeyPem, privateKeyPem, now);

    public void Retire(DateTimeOffset now) => RetiredAt ??= now;

    public void Expire(DateTimeOffset now)
    {
        RetiredAt ??= now;
        ExpiredAt ??= now;
    }
}
