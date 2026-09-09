using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Tenant.Domain.Businesses;

// The tenant root. Our paying customer.
//
// NOTE: this aggregate does NOT implement ITenantScoped, and that is not an
// oversight. Its Id IS the tenant id that all 13 other services store as
// tenant_id. It is the one main table in the system with no tenant filter,
// because the row does not belong to a tenant - it defines one.
//
// Every state transition lives here rather than in a handler, so the rules
// hold no matter which use case runs (docs/05-code-structure.md 2.3).
public sealed class Business : AggregateRoot
{
    private readonly List<BusinessStatusChange> _statusHistory = [];
    private readonly List<EmailVerification> _emailVerifications = [];

    private Business(
        Guid id,
        string legalName,
        string displayName,
        string ownerEmail,
        string ownerName,
        string country,
        DateTimeOffset registeredAt)
        : base(id)
    {
        LegalName = legalName;
        DisplayName = displayName;
        OwnerEmail = ownerEmail;
        OwnerName = ownerName;
        Country = country;
        RegisteredAt = registeredAt;
        Status = BusinessStatus.PendingVerification;
    }

    private Business()
    {
    }

    public string LegalName { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    // Normalised to lowercase on write.
    //
    // Owner@x.com and owner@x.com are the same mailbox to every mail server
    // in practice, so they must not be two businesses. Normalising on write
    // means a plain unique index gives case-insensitive uniqueness - no
    // functional index, no raw SQL in a migration. The cost is that the
    // original casing is not preserved, which nobody has ever needed.
    //
    // That index is the REAL guard; see RegisterBusinessHandler for why the
    // SELECT before it cannot be.
    public string OwnerEmail { get; private set; } = string.Empty;

    public string OwnerName { get; private set; } = string.Empty;

    public string Country { get; private set; } = string.Empty;

    public BusinessStatus Status { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? SuspendedAt { get; private set; }

    public string? SuspensionReason { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public BusinessProfile Profile { get; private set; } = null!;

    public IReadOnlyList<BusinessStatusChange> StatusHistory => _statusHistory;

    public IReadOnlyList<EmailVerification> EmailVerifications => _emailVerifications;

    public static Business Register(
        string legalName,
        string displayName,
        string ownerEmail,
        string ownerName,
        string country,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(country);

        Guid id = Uuid7.New();

        Business business = new(
            id,
            legalName.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? legalName.Trim() : displayName.Trim(),
            ownerEmail.Trim().ToLowerInvariant(),
            ownerName.Trim(),
            country.Trim().ToUpperInvariant(),
            now);

        business.Profile = BusinessProfile.Empty(id);

        business._statusHistory.Add(BusinessStatusChange.Record(
            id, null, BusinessStatus.PendingVerification, "registered", null));

        // Raised here, on the creation path, which is only possible because
        // the id was generated in code rather than by the database
        // (ADR-0010). With an identity column the aggregate would not know
        // its own id until after SaveChanges.
        business.Raise(new BusinessRegistered(
            id, business.LegalName, business.OwnerEmail, business.Country, now));

        return business;
    }

    public EmailVerification IssueEmailVerification(string tokenHash, DateTimeOffset now)
    {
        // A resend invalidates every earlier link, so only the newest email
        // works. Otherwise an old message forwarded to someone else still
        // verifies the account.
        foreach (EmailVerification existing in _emailVerifications)
        {
            if (existing.IsUsable(now))
            {
                existing.Invalidate(now);
            }
        }

        EmailVerification verification = EmailVerification.Issue(Id, tokenHash, now);
        _emailVerifications.Add(verification);
        return verification;
    }

    // Verifying the email activates the business immediately. No human
    // approval step - see BusinessStatus for why.
    public Result VerifyEmail(string tokenHash, DateTimeOffset now)
    {
        if (Status is BusinessStatus.Archived)
        {
            return Result.Failure(BusinessErrors.InvalidTransition(Status, BusinessStatus.Active));
        }

        if (Status is not BusinessStatus.PendingVerification)
        {
            return Result.Failure(BusinessErrors.AlreadyVerified);
        }

        EmailVerification? match = _emailVerifications
            .FirstOrDefault(v => v.TokenHash == tokenHash && !v.IsConsumed && !v.IsInvalidated);

        if (match is null)
        {
            return Result.Failure(BusinessErrors.VerificationTokenInvalid);
        }

        // Expiry is reported separately from "invalid" on purpose: the token
        // was genuinely theirs and the user can act on it by asking for a
        // new link.
        if (match.IsExpired(now))
        {
            return Result.Failure(BusinessErrors.VerificationTokenExpired);
        }

        match.Consume(now);
        VerifiedAt = now;
        TransitionTo(BusinessStatus.Active, "email verified", null, now);
        Raise(new BusinessActivated(Id, LegalName, now));

        return Result.Success();
    }

    // Platform admin un-suspending, per Goal/Domain.txt Part 1.
    public Result Activate(Guid? byUserId, DateTimeOffset now)
    {
        if (Status is not BusinessStatus.Suspended)
        {
            return Result.Failure(BusinessErrors.InvalidTransition(Status, BusinessStatus.Active));
        }

        SuspensionReason = null;
        SuspendedAt = null;
        TransitionTo(BusinessStatus.Active, "reactivated by platform admin", byUserId, now);
        Raise(new BusinessActivated(Id, LegalName, now));

        return Result.Success();
    }

    public Result Suspend(string reason, Guid? byUserId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(BusinessErrors.SuspensionReasonRequired);
        }

        // Two admins both clicking suspend is ordinary, not exceptional -
        // hence a Result rather than an exception.
        if (Status is not BusinessStatus.Active)
        {
            return Result.Failure(BusinessErrors.InvalidTransition(Status, BusinessStatus.Suspended));
        }

        SuspendedAt = now;
        SuspensionReason = reason.Trim();
        TransitionTo(BusinessStatus.Suspended, SuspensionReason, byUserId, now);
        Raise(new BusinessSuspended(Id, SuspensionReason, now));

        return Result.Success();
    }

    public Result Archive(Guid? byUserId, DateTimeOffset now)
    {
        if (Status is BusinessStatus.Archived)
        {
            return Result.Failure(BusinessErrors.InvalidTransition(Status, BusinessStatus.Archived));
        }

        ArchivedAt = now;
        TransitionTo(BusinessStatus.Archived, "archived by platform admin", byUserId, now);
        Raise(new BusinessArchived(Id, now));

        return Result.Success();
    }

    public void UpdateProfile(
        string? legalAddress,
        string? city,
        string? state,
        string? postalCode,
        string? contactPhone,
        string? taxIdentifier,
        string? websiteUrl,
        string? logoUrl)
        => Profile.Update(
            legalAddress, city, state, postalCode, contactPhone, taxIdentifier, websiteUrl, logoUrl);

    private void TransitionTo(BusinessStatus next, string reason, Guid? byUserId, DateTimeOffset now)
    {
        BusinessStatus previous = Status;
        Status = next;

        // First activation only. A reactivation should not overwrite the
        // date the customer originally came on board.
        if (next is BusinessStatus.Active && ActivatedAt is null)
        {
            ActivatedAt = now;
        }

        _statusHistory.Add(BusinessStatusChange.Record(Id, previous, next, reason, byUserId));
    }
}
