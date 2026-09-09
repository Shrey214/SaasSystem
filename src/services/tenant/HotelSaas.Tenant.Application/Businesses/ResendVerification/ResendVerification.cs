using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Application.Businesses.ResendVerification;

public sealed record ResendVerificationCommand(string OwnerEmail);

public sealed class ResendVerificationValidator : AbstractValidator<ResendVerificationCommand>
{
    public ResendVerificationValidator()
        => RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
}

// Issues a fresh verification link.
//
// This one is keyed by EMAIL, not by business id, because the person
// clicking "resend" has lost the email that contained their id.
//
// It always reports success, whatever happened. Unlike registration - where
// the user explicitly accepted a 409 on a duplicate address - this endpoint
// would otherwise let anyone test addresses one at a time with no
// registration attempt and no audit trail.
public sealed class ResendVerificationHandler(
    IBusinessRepository businesses,
    IVerificationTokenGenerator tokens,
    IVerificationDispatcher dispatcher,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result> HandleAsync(
        ResendVerificationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Business? business = await businesses.GetByEmailAsync(command.OwnerEmail, cancellationToken);

        // No such business, or it is already past verification: nothing to
        // send, and we say nothing about which.
        if (business is null || business.Status is not BusinessStatus.PendingVerification)
        {
            return Result.Success();
        }

        DateTimeOffset now = clock.UtcNow;

        // Invalidates every earlier link, so a forwarded old email stops
        // working the moment a new one is requested.
        (string token, string hash) = tokens.Generate();
        business.IssueEmailVerification(hash, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await dispatcher.DispatchAsync(business.Id, business.OwnerEmail, token, cancellationToken);

        return Result.Success();
    }
}
