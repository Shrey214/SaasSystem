using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Application.Businesses.UpdateProfile;

// No BusinessId field, deliberately.
//
// The business being updated is the caller's own, and that comes from
// ITenantContext - i.e. from the validated token. Accepting an id here
// would create the exact parameter ADR-0004 forbids.
public sealed record UpdateProfileCommand(
    string? LegalAddress,
    string? City,
    string? State,
    string? PostalCode,
    string? ContactPhone,
    string? TaxIdentifier,
    string? WebsiteUrl,
    string? LogoUrl);

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.LegalAddress).MaximumLength(500);
        RuleFor(x => x.City).MaximumLength(120);
        RuleFor(x => x.State).MaximumLength(120);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.ContactPhone).MaximumLength(30);
        RuleFor(x => x.TaxIdentifier).MaximumLength(50);

        RuleFor(x => x.WebsiteUrl)
            .MaximumLength(500)
            .Must(BeAnAbsoluteHttpUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.WebsiteUrl))
            .WithMessage("Must be an absolute http or https URL.");

        RuleFor(x => x.LogoUrl)
            .MaximumLength(500)
            .Must(BeAnAbsoluteHttpUrl)
            .When(x => !string.IsNullOrWhiteSpace(x.LogoUrl))
            .WithMessage("Must be an absolute http or https URL.");
    }

    // Absolute and http(s) only. A relative or javascript: URL rendered on
    // a property website later is a stored-XSS vector, and the cheapest
    // place to refuse it is on the way in.
    private static bool BeAnAbsoluteHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class UpdateProfileHandler(
    IBusinessRepository businesses,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        UpdateProfileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Guid businessId = tenantContext.RequireTenantId();

        Business? business = await businesses.GetByIdAsync(businessId, cancellationToken);

        if (business is null)
        {
            return Result.Failure(BusinessErrors.NotFound);
        }

        business.UpdateProfile(
            command.LegalAddress,
            command.City,
            command.State,
            command.PostalCode,
            command.ContactPhone,
            command.TaxIdentifier,
            command.WebsiteUrl,
            command.LogoUrl);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
