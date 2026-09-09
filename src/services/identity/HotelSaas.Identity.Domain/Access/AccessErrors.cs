using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Identity.Domain.Access;

// Every failure this service can return, in one place. Codes are part of
// the API contract and do not change once shipped.
public static class AccessErrors
{
    // Deliberately identical for a wrong email and a wrong password. Two
    // different messages turn the login form into an account-existence
    // oracle, which is the one place enumeration actually matters.
    public static readonly Error InvalidCredentials = Error.Unauthorized(
        "identity.invalid_credentials",
        "Email or password is incorrect.");

    public static readonly Error AccountLocked = Error.Forbidden(
        "identity.account_locked",
        "Too many failed attempts. Try again later.");

    public static readonly Error PasswordNotSet = Error.Conflict(
        "identity.password_not_set",
        "This account has not finished setup. Use the invitation link that was emailed to you.");

    public static readonly Error InvitationInvalid = Error.Conflict(
        "identity.invitation_invalid",
        "This invitation link is not valid.");

    public static readonly Error InvitationExpired = Error.Conflict(
        "identity.invitation_expired",
        "This invitation link has expired. Ask for a new one.");

    public static readonly Error InvitationAlreadyAccepted = Error.Conflict(
        "identity.invitation_already_accepted",
        "This invitation has already been used.");

    public static readonly Error InvitationRevoked = Error.Conflict(
        "identity.invitation_revoked",
        "This invitation has been withdrawn.");

    public static readonly Error EmailAlreadyHasAccount = Error.Conflict(
        "identity.email_already_has_account",
        "An account already exists for this email address.");

    public static readonly Error RefreshTokenInvalid = Error.Unauthorized(
        "identity.refresh_token_invalid",
        "This session is no longer valid. Sign in again.");

    // Presenting an ALREADY-ROTATED token means someone holds a copy they
    // should not. The whole device chain is revoked, and the user is told
    // to sign in again rather than being quietly logged out.
    public static readonly Error RefreshTokenReplayed = Error.Unauthorized(
        "identity.refresh_token_replayed",
        "This session was ended for security reasons. Sign in again.");

    public static readonly Error CannotRemoveOwner = Error.RuleViolation(
        "identity.cannot_remove_owner",
        "The business owner's access cannot be removed.");

    public static readonly Error MembershipAlreadyRemoved = Error.Conflict(
        "identity.membership_already_removed",
        "This user's access has already been removed.");

    public static readonly Error NoSigningKey = Error.External(
        "identity.no_signing_key",
        "No active signing key. The service cannot issue tokens.");
}
