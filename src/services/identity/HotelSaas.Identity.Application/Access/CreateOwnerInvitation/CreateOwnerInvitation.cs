using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;

namespace HotelSaas.Identity.Application.Access.CreateOwnerInvitation;

// Called by the tenant service when a business finishes email verification.
//
// This is the ONE synchronous cross-service call in the signup path. It
// lives behind /internal, is never exposed through the gateway, and if it
// fails the verification fails with it - a business that is Active but
// whose owner can never sign in is worse than one that has to click the
// link twice.
public sealed record CreateOwnerInvitationCommand(
    Guid TenantId,
    string OwnerEmail,
    string OwnerName);

public sealed record CreateOwnerInvitationResponse(
    Guid InvitationId,
    Guid UserId,
    DateTimeOffset ExpiresAt,
    // Returned once, to whoever asked. Stage 16 will have notification
    // email it instead; until then tenant hands it back so the flow can be
    // walked end to end.
    string InvitationToken);

public sealed class CreateOwnerInvitationValidator : AbstractValidator<CreateOwnerInvitationCommand>
{
    public CreateOwnerInvitationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.OwnerName).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateOwnerInvitationHandler(
    IUserAccounts users,
    IInvitationRepository invitations,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<CreateOwnerInvitationResponse>> HandleAsync(
        CreateOwnerInvitationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset now = clock.UtcNow;

        // Idempotent on purpose. tenant may retry this after a timeout, and
        // the second attempt must not fail - it must hand back a usable
        // invitation. Without this, a network blip during signup would
        // leave a business with no way to ever create its owner.
        UserRecord? existing = await users.FindByEmailAsync(command.OwnerEmail, cancellationToken);

        if (existing is not null)
        {
            if (existing.HasPassword)
            {
                // The account is already set up. Not our problem to solve
                // here - the owner should sign in, or use a password reset.
                return AccessErrors.EmailAlreadyHasAccount;
            }

            Invitation? open = await invitations.FindOpenForEmailAsync(
                command.TenantId, command.OwnerEmail, cancellationToken);

            if (open is not null)
            {
                // A live invitation exists but we only stored its hash, so
                // the original token is unrecoverable. Revoke it and issue
                // a fresh one - the old link stops working, which is also
                // what you want if the first email went astray.
                open.Revoke(now);
            }

            return await IssueAsync(existing.Id, command, now, cancellationToken);
        }

        Result<Guid> created = await users.CreateWithoutPasswordAsync(
            command.OwnerEmail,
            command.OwnerName,
            scopeType: "tenant",
            cancellationToken);

        if (created.IsFailure)
        {
            return Result.Failure<CreateOwnerInvitationResponse>(created.Error);
        }

        return await IssueAsync(created.Value, command, now, cancellationToken);
    }

    private async Task<Result<CreateOwnerInvitationResponse>> IssueAsync(
        Guid userId,
        CreateOwnerInvitationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        (string token, string hash) = SecureToken.Create();

        Invitation invitation = Invitation.ForOwner(command.TenantId, command.OwnerEmail, hash, now);
        invitations.Add(invitation);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateOwnerInvitationResponse(
            invitation.Id,
            userId,
            invitation.ExpiresAt,
            token));
    }
}
