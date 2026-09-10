using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;

namespace HotelSaas.Identity.Application.Access.AcceptInvitation;

// The only way a password enters this system.
//
// The tenant service never sees one. It creates the invitation (via the
// internal endpoint) and the invitee brings their password straight here.
public sealed record AcceptInvitationCommand(string Token, string Password);

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);

        // Length only. The real policy lives in Identity's password
        // validators (12 chars, no character classes - see
        // InfrastructureServiceCollectionExtensions), and duplicating it
        // here would mean two rules to keep in step. This just refuses the
        // obviously-empty case before doing any work.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(256);
    }
}

public sealed class AcceptInvitationHandler(
    IInvitationRepository invitations,
    IMembershipRepository memberships,
    IUserAccounts users,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result> HandleAsync(
        AcceptInvitationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The incoming token is hashed and the HASH is looked up. The token
        // itself was never stored, so there is nothing else to match on.
        Invitation? invitation = await invitations.FindByTokenHashAsync(
            SecureToken.HashOf(command.Token), cancellationToken);

        if (invitation is null)
        {
            return Result.Failure(AccessErrors.InvitationInvalid);
        }

        DateTimeOffset now = clock.UtcNow;

        // The aggregate decides, and reports expiry separately from
        // "invalid" - the link genuinely was theirs and they can fix it by
        // asking for another.
        Result accepted = invitation.Accept(now);

        if (accepted.IsFailure)
        {
            return accepted;
        }

        UserRecord? user = await users.FindByEmailAsync(invitation.Email, cancellationToken);

        if (user is null)
        {
            // The invitation exists but its user does not. Only reachable
            // if something deleted the user directly, which nothing does.
            return Result.Failure(AccessErrors.InvitationInvalid);
        }

        if (user.HasPassword)
        {
            // Belt and braces: the invitation should already have been
            // marked accepted in that case.
            return Result.Failure(AccessErrors.InvitationAlreadyAccepted);
        }

        // Runs Identity's password validators and its hasher. A weak
        // password fails HERE, after the invitation was marked accepted in
        // memory but before anything is committed - so the transaction
        // below never happens and the link stays usable.
        Result passwordSet = await users.SetPasswordAsync(user.Id, command.Password, cancellationToken);

        if (passwordSet.IsFailure)
        {
            return passwordSet;
        }

        // First user of a business becomes its owner. Owner invitations are
        // only ever created by tenant, at verification, so this cannot be
        // used to grant ownership of somebody else's business.
        memberships.Add(invitation.IsOwnerInvitation
            ? UserTenantMembership.ForOwner(user.Id, invitation.TenantId, now)
            : UserTenantMembership.ForStaff(user.Id, invitation.TenantId, now));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
