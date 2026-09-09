using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Tenant.Domain.Businesses;

// Every failure this service can return, in one place.
//
// Codes are part of the API contract: the frontend switches on them and
// translates them, so they do not change once shipped.
public static class BusinessErrors
{
    public static readonly Error EmailAlreadyRegistered = Error.Conflict(
        "tenant.email_already_registered",
        "A business is already registered with this email address.");

    public static readonly Error NotFound = Error.NotFound(
        "tenant.business_not_found",
        "No such business.");

    public static readonly Error VerificationTokenInvalid = Error.Conflict(
        "tenant.verification_token_invalid",
        "This verification link is not valid.");

    public static readonly Error VerificationTokenExpired = Error.Conflict(
        "tenant.verification_token_expired",
        "This verification link has expired. Request a new one.");

    public static readonly Error AlreadyVerified = Error.Conflict(
        "tenant.already_verified",
        "This business has already been verified.");

    public static readonly Error SuspensionReasonRequired = Error.Validation(
        "tenant.suspension_reason_required",
        "A reason is required when suspending a business.");

    public static Error InvalidTransition(BusinessStatus from, BusinessStatus to) => Error.Conflict(
        "tenant.invalid_status_transition",
        $"A business cannot go from {from} to {to}.");
}
