using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Domain.Access;

namespace HotelSaas.Identity.Application.Abstractions;

// What the use cases need from the outside. Implementations in
// Infrastructure, so Application never references EF Core or ASP.NET Core
// Identity.

// A user, as the use cases see it.
//
// Not ApplicationUser: that derives from IdentityUser, which is a framework
// type Application must not know about. This is the projection of it that
// the use cases actually need.
public sealed record UserRecord(
    Guid Id,
    string Email,
    string FullName,
    string ScopeType,
    int PermissionsVersion,
    bool HasPassword);

// Everything that touches the credential store.
//
// This interface exists so the UserManager - password hashing, lockout,
// normalisation - stays behind a boundary. Its methods are deliberately
// coarse: "verify these credentials" rather than "give me the hash", so
// there is no way for a use case to do the comparison itself and get it
// wrong.
public interface IUserAccounts
{
    Task<UserRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    // Creates a user who cannot sign in yet - no password. That is the state
    // between "invited" and "set up".
    Task<Result<Guid>> CreateWithoutPasswordAsync(
        string email,
        string fullName,
        string scopeType,
        CancellationToken cancellationToken);

    Task<Result> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken);

    // Returns the user on success. On failure returns invalid_credentials or
    // account_locked and NOTHING else - the caller cannot tell a wrong
    // password from a missing account, which is the point.
    Task<Result<UserRecord>> VerifyPasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    Task RecordSuccessfulLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken);
}

public interface IInvitationRepository
{
    Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<Invitation?> FindOpenForEmailAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken);

    void Add(Invitation invitation);
}

public interface IMembershipRepository
{
    Task<UserTenantMembership?> FindActiveForUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(UserTenantMembership membership);
}

// The RS256 key pair used to sign and verify access tokens.
public interface ISigningKeyStore
{
    // The key to sign with. Creates one on first use rather than failing:
    // a fresh deployment with no key can issue no tokens at all, and a
    // service that will not start is worse than one that generates its own.
    Task<SigningKey> GetOrCreateActiveAsync(CancellationToken cancellationToken);

    // Every key still accepting verification, for JWKS. Includes retired
    // keys, because tokens they signed are still valid until they expire.
    Task<IReadOnlyList<SigningKey>> GetVerificationKeysAsync(CancellationToken cancellationToken);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, string KeyId);

public interface IAccessTokenIssuer
{
    Task<AccessToken> IssueAsync(
        UserRecord user,
        Guid? tenantId,
        CancellationToken cancellationToken);
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // Who signed it. Every service validating a token checks this, so it
    // must match across all 14.
    public string Issuer { get; set; } = "hotelsaas-identity";

    // Who it is for. A token minted for one audience must not be accepted
    // by another - this is what stops a token issued for an internal tool
    // being replayed against the customer API.
    public string Audience { get; set; } = "hotelsaas";

    // Short on purpose. This is the hard ceiling on how stale the
    // permissions inside a token can be, because nothing can un-issue one
    // (ADR-0005).
    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 14;
}
