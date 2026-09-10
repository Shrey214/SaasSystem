using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Identity.Infrastructure.Persistence;

internal sealed class InvitationRepository(IdentityServiceDbContext db) : IInvitationRepository
{
    // IgnoreQueryFilters on purpose.
    //
    // Invitation is ITenantScoped, but this lookup happens BEFORE anyone is
    // authenticated - the whole point is that the holder of the token is
    // not yet a user of any tenant. The token hash is the authorisation
    // here: 256 bits, unique index, single use.
    public Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
        => db.Invitations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

    // Also unfiltered: called from the internal endpoint, where tenant
    // supplies the tenant id and no token exists yet to carry it.
    public Task<Invitation?> FindOpenForEmailAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken)
    {
        string normalised = email.Trim().ToLowerInvariant();

        return db.Invitations
            .IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId
                        && i.Email == normalised
                        && i.AcceptedAt == null
                        && i.RevokedAt == null)
            .OrderByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void Add(Invitation invitation) => db.Invitations.Add(invitation);
}

internal sealed class MembershipRepository(IdentityServiceDbContext db) : IMembershipRepository
{
    // Unfiltered because this runs DURING login, before a tenant context
    // exists - looking up the membership is how we learn which tenant to
    // put in the token in the first place. Scoped by user id instead.
    public Task<UserTenantMembership?> FindActiveForUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
        => db.UserTenantMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.RemovedAt == null, cancellationToken);

    public void Add(UserTenantMembership membership) => db.UserTenantMemberships.Add(membership);
}
