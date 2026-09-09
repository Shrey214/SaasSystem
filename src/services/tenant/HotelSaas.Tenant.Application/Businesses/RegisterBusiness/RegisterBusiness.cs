using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.Extensions.Logging;

namespace HotelSaas.Tenant.Application.Businesses.RegisterBusiness;

public sealed record RegisterBusinessCommand(
    string LegalName,
    string? DisplayName,
    string OwnerEmail,
    string OwnerName,
    string Country);

public sealed record RegisterBusinessResponse(
    Guid BusinessId,
    string Status,
    // Development only, so the flow can be walked without an email service.
    // Null everywhere else - see VerificationDispatcher.
    string? VerificationToken);

public sealed class RegisterBusinessValidator : AbstractValidator<RegisterBusinessCommand>
{
    public RegisterBusinessValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).MaximumLength(200);
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.OwnerName).NotEmpty().MaximumLength(200);

        // ISO 3166-1 alpha-2. A two-letter code is checked here; whether it
        // is a real country is checked against the currencies/countries
        // reference table, not by a regex.
        RuleFor(x => x.Country).NotEmpty().Length(2);
    }
}

public sealed class RegisterBusinessHandler(
    IBusinessRepository businesses,
    IVerificationTokenGenerator tokens,
    IVerificationDispatcher dispatcher,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RegisterBusinessHandler> logger)
{
    public async Task<Result<RegisterBusinessResponse>> HandleAsync(
        RegisterBusinessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A friendly early check. NOT the guard - see the catch below. Two
        // simultaneous registrations of the same email both pass this.
        if (await businesses.EmailExistsAsync(command.OwnerEmail, cancellationToken))
        {
            return BusinessErrors.EmailAlreadyRegistered;
        }

        DateTimeOffset now = clock.UtcNow;

        Business business = Business.Register(
            command.LegalName,
            command.DisplayName ?? string.Empty,
            command.OwnerEmail,
            command.OwnerName,
            command.Country,
            now);

        (string token, string hash) = tokens.Generate();
        business.IssueEmailVerification(hash, now);

        businesses.Add(business);

        try
        {
            // One transaction writes the business, its empty profile, the
            // first status-history row, the hashed token AND the outbox row
            // for tenant.business.registered.v1 (ADR-0006).
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsDuplicateEmail(ex))
        {
            // THE actual uniqueness guard.
            //
            // The check above is a race: both requests SELECT, both find
            // nothing, both INSERT. Only the unique index on
            // lower(owner_email) is atomic, so the constraint violation is
            // the real answer and this converts it into the same 409 the
            // early check would have produced.
            TenantLog.DuplicateEmailRace(logger, business.Id);
            return BusinessErrors.EmailAlreadyRegistered;
        }

        await dispatcher.DispatchAsync(business.Id, business.OwnerEmail, token, cancellationToken);

        return Result.Success(new RegisterBusinessResponse(
            business.Id,
            business.Status.ToString(),
            dispatcher.ExposesTokens ? token : null));
    }

    // Npgsql reports a unique-violation as SqlState 23505. Matched without
    // referencing Npgsql, so Application stays free of the data provider.
    private static bool IsDuplicateEmail(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().GetProperty("SqlState")?.GetValue(current) is "23505")
            {
                return true;
            }
        }

        return false;
    }
}

internal static partial class TenantLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Registration for business {BusinessId} lost the unique-email race; returning 409")]
    public static partial void DuplicateEmailRace(ILogger logger, Guid businessId);
}
