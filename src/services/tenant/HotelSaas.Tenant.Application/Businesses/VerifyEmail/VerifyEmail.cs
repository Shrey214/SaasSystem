using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Application.Businesses.VerifyEmail;

public sealed record VerifyEmailCommand(Guid BusinessId, string Token);

public sealed record VerifyEmailResponse(Guid BusinessId, string Status);

public sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailValidator()
    {
        RuleFor(x => x.BusinessId).NotEmpty();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
    }
}

// Verifying the email is what activates the business. Self-service, no
// approval step (see BusinessStatus).
public sealed class VerifyEmailHandler(
    IBusinessRepository businesses,
    IVerificationTokenGenerator tokens,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<VerifyEmailResponse>> HandleAsync(
        VerifyEmailCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Business? business = await businesses.GetByIdAsync(command.BusinessId, cancellationToken);

        if (business is null)
        {
            return BusinessErrors.NotFound;
        }

        // The incoming token is hashed and the HASH is compared. The plain
        // token is never stored, so there is nothing to compare it against.
        string hash = tokens.Hash(command.Token);

        // Two transitions and an audit row, all inside the aggregate, so the
        // rules cannot be bypassed by a different use case later.
        Result result = business.VerifyEmail(hash, clock.UtcNow);

        if (result.IsFailure)
        {
            return Result.Failure<VerifyEmailResponse>(result.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new VerifyEmailResponse(business.Id, business.Status.ToString()));
    }
}
