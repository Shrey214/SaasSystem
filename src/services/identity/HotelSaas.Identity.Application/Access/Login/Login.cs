using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;

namespace HotelSaas.Identity.Application.Access.Login;

public sealed record LoginCommand(string Email, string Password);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string TokenType,
    Guid? TenantId,
    string ScopeType);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        // Deliberately loose. A login form must not tell an attacker that
        // an address is malformed in a different way to a wrong password,
        // and length rules here would leak the password policy.
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public sealed class LoginHandler(
    IUserAccounts users,
    IMembershipRepository memberships,
    IAccessTokenIssuer tokens,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<LoginResponse>> HandleAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One call, one answer. Wrong email and wrong password come back
        // identical - two different messages turn this endpoint into an
        // account-existence oracle, which is the one place enumeration
        // genuinely matters.
        Result<UserRecord> verified = await users.VerifyPasswordAsync(
            command.Email, command.Password, cancellationToken);

        if (verified.IsFailure)
        {
            return Result.Failure<LoginResponse>(verified.Error);
        }

        UserRecord user = verified.Value;

        // A platform admin has no membership row at all, so tenant_id stays
        // null and scope_type carries the distinction. Every service treats
        // a null tenant as platform scope and fails closed on tenant data
        // (ADR-0004).
        UserTenantMembership? membership = user.ScopeType == "platform"
            ? null
            : await memberships.FindActiveForUserAsync(user.Id, cancellationToken);

        AccessToken token = await tokens.IssueAsync(user, membership?.TenantId, cancellationToken);

        await users.RecordSuccessfulLoginAsync(user.Id, clock.UtcNow, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // No refresh token yet - part 3. Until then a session simply ends
        // when the access token expires, which is honest rather than
        // half-implemented.
        return Result.Success(new LoginResponse(
            token.Value,
            token.ExpiresAt,
            "Bearer",
            membership?.TenantId,
            user.ScopeType));
    }
}
