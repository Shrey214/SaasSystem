using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Identity.Domain.Access;

// The authorization aggregates.
//
// NOTE what is NOT here: the user record itself. ApplicationUser derives
// from ASP.NET Core Identity's IdentityUser, which is a framework type, and
// Domain has zero package references by rule (docs/00-conventions.md 2). So
// the credential store lives in Infrastructure and Domain owns only the
// things with real rules - who belongs to which tenant, which invitations
// are still open, which refresh tokens are still live.
//
// user_id is a plain Guid here. Domain cannot see ApplicationUser and does
// not need to.

// Which tenant a user belongs to, and whether they own it.
//
// A user belongs to exactly one tenant in practice, but this is a separate
// table rather than a column on the user because Goal/Saas.txt 5 has staff
// who may later be moved, and because platform admins belong to NO tenant -
// a nullable column on every user would make the platform case the odd one
// out on every query.
public sealed class UserTenantMembership : Entity, ITenantScoped
{
    private UserTenantMembership(Guid id, Guid userId, Guid tenantId, bool isOwner, DateTimeOffset joinedAt)
        : base(id)
    {
        UserId = userId;
        TenantId = tenantId;
        IsOwner = isOwner;
        JoinedAt = joinedAt;
    }

    private UserTenantMembership()
    {
    }

    public Guid UserId { get; private set; }

    // No foreign key - the business row lives in hs_tenant (ADR-0003).
    public Guid TenantId { get; set; }

    // The first user of a business. Cannot be removed by another staff
    // member, and cannot have their own access revoked.
    public bool IsOwner { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsActive => RemovedAt is null;

    public static UserTenantMembership ForOwner(Guid userId, Guid tenantId, DateTimeOffset now)
        => new(Uuid7.New(), userId, tenantId, isOwner: true, now);

    public static UserTenantMembership ForStaff(Guid userId, Guid tenantId, DateTimeOffset now)
        => new(Uuid7.New(), userId, tenantId, isOwner: false, now);

    public Result Remove(DateTimeOffset now)
    {
        if (IsOwner)
        {
            return Result.Failure(AccessErrors.CannotRemoveOwner);
        }

        if (!IsActive)
        {
            return Result.Failure(AccessErrors.MembershipAlreadyRemoved);
        }

        RemovedAt = now;
        return Result.Success();
    }
}

// A one-time link that lets somebody set a password and become a user.
//
// The OWNER uses this too. Goal/Domain.txt Part 1 needs "Invite Staff"
// anyway, so building it once and letting the owner be the first invitee
// avoids a second, parallel signup path - and keeps the rule that matters:
// a password never touches the tenant service.
public sealed class Invitation : Entity, ITenantScoped
{
    public const int ValidForHours = 72;

    private Invitation(
        Guid id,
        Guid tenantId,
        string email,
        string tokenHash,
        bool isOwnerInvitation,
        Guid? invitedByUserId,
        DateTimeOffset expiresAt)
        : base(id)
    {
        TenantId = tenantId;
        Email = email;
        TokenHash = tokenHash;
        IsOwnerInvitation = isOwnerInvitation;
        InvitedByUserId = invitedByUserId;
        ExpiresAt = expiresAt;
    }

    private Invitation()
    {
    }

    public Guid TenantId { get; set; }

    // Normalised lowercase, same as the tenant service, so the two agree
    // about what "the same mailbox" means.
    public string Email { get; private set; } = string.Empty;

    // SHA-256 hex. The token itself is never stored.
    public string TokenHash { get; private set; } = string.Empty;

    public bool IsOwnerInvitation { get; private set; }

    // Null when the SYSTEM invited them - which is the owner's own case,
    // since no user existed yet to do the inviting.
    public Guid? InvitedByUserId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    // Longer than the tenant service's 24h email verification: this one is
    // the last step of onboarding and a business owner may well leave it
    // until Monday.
    public static Invitation ForOwner(Guid tenantId, string email, string tokenHash, DateTimeOffset now)
        => new(Uuid7.New(), tenantId, Normalise(email), tokenHash, true, null, now.AddHours(ValidForHours));

    public static Invitation ForStaff(
        Guid tenantId,
        string email,
        string tokenHash,
        Guid invitedByUserId,
        DateTimeOffset now)
        => new(Uuid7.New(), tenantId, Normalise(email), tokenHash, false, invitedByUserId, now.AddHours(ValidForHours));

    public bool IsUsable(DateTimeOffset now)
        => AcceptedAt is null && RevokedAt is null && now < ExpiresAt;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public Result Accept(DateTimeOffset now)
    {
        if (AcceptedAt is not null)
        {
            return Result.Failure(AccessErrors.InvitationAlreadyAccepted);
        }

        if (RevokedAt is not null)
        {
            return Result.Failure(AccessErrors.InvitationRevoked);
        }

        if (IsExpired(now))
        {
            return Result.Failure(AccessErrors.InvitationExpired);
        }

        AcceptedAt = now;
        return Result.Success();
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
