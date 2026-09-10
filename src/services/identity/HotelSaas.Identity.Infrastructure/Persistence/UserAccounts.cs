using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;
using Microsoft.AspNetCore.Identity;

namespace HotelSaas.Identity.Infrastructure.Persistence;

// Everything that touches the credential store, behind one boundary.
//
// UserManager does the parts that must not be hand-written: PBKDF2 hashing,
// lockout counting, email normalisation, the constant-time comparison. The
// only thing added here is translating its results into our Result and
// Error types so the use cases never see an IdentityResult.
internal sealed class UserAccounts(
    UserManager<ApplicationUser> userManager,
    IdentityServiceDbContext db) : IUserAccounts
{
    public async Task<UserRecord?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        return user is null ? null : ToRecord(user);
    }

    public async Task<Result<Guid>> CreateWithoutPasswordAsync(
        string email,
        string fullName,
        string scopeType,
        CancellationToken cancellationToken)
    {
        // UserName is set to the email. This service has no separate
        // username concept and never will - one identifier is one fewer
        // thing to get out of step.
        ApplicationUser user = new()
        {
            Id = Uuid7.New(),
            UserName = email,
            Email = email,
            FullName = fullName,
            ScopeType = scopeType,
            EmailConfirmed = true,
        };

        // No password. The user exists and cannot sign in - exactly the
        // state between "invited" and "set up".
        IdentityResult result = await userManager.CreateAsync(user);

        if (!result.Succeeded)
        {
            return Result.Failure<Guid>(TranslateCreate(result));
        }

        return Result.Success(user.Id);
    }

    public async Task<Result> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());

        if (user is null)
        {
            return Result.Failure(AccessErrors.InvitationInvalid);
        }

        // AddPasswordAsync, not ResetPassword: this user has no password
        // yet, and it runs the configured validators, so a weak password is
        // refused here rather than stored.
        IdentityResult result = await userManager.AddPasswordAsync(user, password);

        if (!result.Succeeded)
        {
            // The messages come from Identity's validators and describe the
            // policy the caller just failed, which is safe to show: the
            // policy is not a secret.
            return Result.Failure(Error.Validation(
                "identity.password_rejected",
                string.Join(" ", result.Errors.Select(e => e.Description))));
        }

        user.PasswordSetAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result<UserRecord>> VerifyPasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(email);

        // No such account. Returns the SAME error as a wrong password, so
        // the caller cannot tell them apart.
        //
        // The timing difference is real and not addressed here: a missing
        // account skips the hash comparison and answers faster. Closing
        // that properly means hashing a dummy password anyway, and the
        // honest mitigation for now is the lockout plus Kong's rate
        // limiting at 5c.
        if (user is null)
        {
            return AccessErrors.InvalidCredentials;
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return AccessErrors.AccountLocked;
        }

        if (user.PasswordHash is null)
        {
            // Invited but never finished setup. Told plainly, because the
            // person is legitimate and the useful next step is the link in
            // their inbox - and it reveals nothing an invitee does not
            // already know.
            return AccessErrors.PasswordNotSet;
        }

        // CheckPasswordAsync does NOT count failures. That is what
        // AccessFailedAsync is for, and forgetting it is how a login
        // endpoint ends up with a lockout policy that never triggers.
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return AccessErrors.InvalidCredentials;
        }

        await userManager.ResetAccessFailedCountAsync(user);

        return Result.Success(ToRecord(user));
    }

    public async Task RecordSuccessfulLoginAsync(
        Guid userId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ApplicationUser? user = await db.Users.FindAsync([userId], cancellationToken);

        if (user is not null)
        {
            user.LastLoginAt = at;
        }
    }

    private static UserRecord ToRecord(ApplicationUser user) => new(
        user.Id,
        user.Email ?? string.Empty,
        user.FullName,
        user.ScopeType,
        user.PermissionsVersion,
        user.PasswordHash is not null);

    private static Error TranslateCreate(IdentityResult result)
        => result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
            ? AccessErrors.EmailAlreadyHasAccount
            : Error.Validation(
                "identity.user_rejected",
                string.Join(" ", result.Errors.Select(e => e.Description)));
}
