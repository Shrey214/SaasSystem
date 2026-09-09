using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Application.PlatformAdmin;

// Platform-admin operations: our side of the SaaS.
//
// These take a business id in the command, unlike the tenant-scoped use
// cases. That is the audited cross-tenant path from ADR-0004 - a separate
// route prefix and a separate handler, never a flag on the normal one.
//
// Goal/Domain.txt Part 1 lists "Business Activation | Platform Admin" whose
// rule reads "suspended businesses cannot use platform" - so Activate here
// means UN-suspend, not approve a new signup.

public sealed record SuspendBusinessCommand(Guid BusinessId, string Reason);

public sealed class SuspendBusinessValidator : AbstractValidator<SuspendBusinessCommand>
{
    public SuspendBusinessValidator()
    {
        RuleFor(x => x.BusinessId).NotEmpty();

        // Required, and long enough to be a real reason. This ends up in
        // business_status_history and is the only record of why a paying
        // customer was cut off.
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500);
    }
}

public sealed class SuspendBusinessHandler(
    IBusinessRepository businesses,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result> HandleAsync(
        SuspendBusinessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Business? business = await businesses.GetByIdAsync(command.BusinessId, cancellationToken);

        if (business is null)
        {
            return Result.Failure(BusinessErrors.NotFound);
        }

        Result result = business.Suspend(command.Reason, currentUser.UserId, clock.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ActivateBusinessCommand(Guid BusinessId);

public sealed class ActivateBusinessHandler(
    IBusinessRepository businesses,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result> HandleAsync(
        ActivateBusinessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Business? business = await businesses.GetByIdAsync(command.BusinessId, cancellationToken);

        if (business is null)
        {
            return Result.Failure(BusinessErrors.NotFound);
        }

        Result result = business.Activate(currentUser.UserId, clock.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ArchiveBusinessCommand(Guid BusinessId);

public sealed class ArchiveBusinessHandler(
    IBusinessRepository businesses,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result> HandleAsync(
        ArchiveBusinessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Business? business = await businesses.GetByIdAsync(command.BusinessId, cancellationToken);

        if (business is null)
        {
            return Result.Failure(BusinessErrors.NotFound);
        }

        // Terminal, and still not a delete: Goal/Domain.txt Part 1 requires
        // historical bookings to survive, and they reference this tenant id
        // from another database with no foreign key to protect them.
        Result result = business.Archive(currentUser.UserId, clock.UtcNow);

        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record SearchBusinessesQuery(
    string? Query,
    BusinessStatus? Status,
    string? Cursor,
    int Limit = 25);

public sealed class SearchBusinessesHandler(IBusinessQueries queries)
{
    private const int MaxLimit = 100;

    public Task<PagedResult<BusinessSummary>> HandleAsync(
        SearchBusinessesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Clamped rather than validated: a client asking for 10,000 rows
        // gets 100, not an error. A page size is not a business rule.
        int limit = Math.Clamp(query.Limit, 1, MaxLimit);

        return queries.SearchAsync(
            new BusinessSearchCriteria(query.Query, query.Status, query.Cursor, limit),
            cancellationToken);
    }
}
