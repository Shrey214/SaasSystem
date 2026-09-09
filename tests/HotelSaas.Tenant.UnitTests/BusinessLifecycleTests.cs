using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.UnitTests;

// The state machine, tested without a database.
//
// This is what the Domain project having zero package references buys: the
// rules are plain C# and run in milliseconds.
public sealed class BusinessLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    private static Business NewRegistered(out string tokenHash)
    {
        Business business = Business.Register(
            "Porwal Hotels Pvt Ltd", "Porwal Hotels", "Owner@PorwalHotels.dev", "Shreyash", "in", Now);

        tokenHash = "a".PadRight(64, 'b');
        business.IssueEmailVerification(tokenHash, Now);
        return business;
    }

    [Fact]
    public void Register_StartsPendingVerification_AndRaisesRegistered()
    {
        Business business = Business.Register(
            "Royal Hospitality", null!, "owner@royal.dev", "Owner", "IN", Now);

        business.Status.ShouldBe(BusinessStatus.PendingVerification);
        business.RegisteredAt.ShouldBe(Now);
        business.VerifiedAt.ShouldBeNull();
        business.ActivatedAt.ShouldBeNull();

        business.DomainEvents.OfType<BusinessRegistered>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Register_NormalisesEmailToLowercase()
    {
        // A plain unique index only gives case-insensitive uniqueness if the
        // stored value is normalised. Owner@x.com and owner@x.com are the
        // same mailbox and must not be two businesses.
        Business business = Business.Register(
            "X", "X", "  Owner@PorwalHotels.DEV  ", "Owner", "in", Now);

        business.OwnerEmail.ShouldBe("owner@porwalhotels.dev");
        business.Country.ShouldBe("IN");
    }

    [Fact]
    public void Register_DefaultsDisplayNameToLegalName()
    {
        Business business = Business.Register("Royal Hospitality", "   ", "o@r.dev", "O", "IN", Now);

        business.DisplayName.ShouldBe("Royal Hospitality");
    }

    [Fact]
    public void Register_RecordsTheFirstStatusHistoryEntry_WithNoPreviousStatus()
    {
        Business business = Business.Register("X", "X", "o@x.dev", "O", "IN", Now);

        BusinessStatusChange first = business.StatusHistory.ShouldHaveSingleItem();
        first.FromStatus.ShouldBeNull();
        first.ToStatus.ShouldBe(BusinessStatus.PendingVerification);
    }

    // ---- verification ----

    [Fact]
    public void VerifyEmail_WithTheRightToken_ActivatesImmediately()
    {
        // Self-service: no platform-admin approval step.
        Business business = NewRegistered(out string hash);

        business.VerifyEmail(hash, Now).IsSuccess.ShouldBeTrue();

        business.Status.ShouldBe(BusinessStatus.Active);
        business.VerifiedAt.ShouldBe(Now);
        business.ActivatedAt.ShouldBe(Now);
        business.DomainEvents.OfType<BusinessActivated>().ShouldHaveSingleItem();
    }

    [Fact]
    public void VerifyEmail_WithAnUnknownToken_IsRefused()
    {
        Business business = NewRegistered(out _);

        Result result = business.VerifyEmail("not-the-stored-hash", Now);

        result.Error.ShouldBe(BusinessErrors.VerificationTokenInvalid);
        business.Status.ShouldBe(BusinessStatus.PendingVerification);
    }

    [Fact]
    public void VerifyEmail_AfterExpiry_ReportsExpiredRatherThanInvalid()
    {
        // Reported separately on purpose: the token WAS theirs, and the user
        // can act on it by requesting a new link. "Invalid" would send them
        // to support instead.
        Business business = NewRegistered(out string hash);

        Result result = business.VerifyEmail(hash, Now.AddHours(EmailVerification.ValidForHours + 1));

        result.Error.ShouldBe(BusinessErrors.VerificationTokenExpired);
        business.Status.ShouldBe(BusinessStatus.PendingVerification);
    }

    [Fact]
    public void VerifyEmail_Twice_IsRefused()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);

        business.VerifyEmail(hash, Now).Error.ShouldBe(BusinessErrors.AlreadyVerified);
    }

    [Fact]
    public void IssueEmailVerification_InvalidatesEveryEarlierToken()
    {
        // Otherwise an old email, forwarded to somebody else, still verifies
        // the account.
        Business business = NewRegistered(out string firstHash);

        string secondHash = "c".PadRight(64, 'd');
        business.IssueEmailVerification(secondHash, Now.AddMinutes(5));

        business.VerifyEmail(firstHash, Now.AddMinutes(6))
            .Error.ShouldBe(BusinessErrors.VerificationTokenInvalid);

        business.VerifyEmail(secondHash, Now.AddMinutes(6)).IsSuccess.ShouldBeTrue();
    }

    // ---- platform admin ----

    [Fact]
    public void Suspend_RequiresAReason()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);

        business.Suspend("   ", null, Now).Error.ShouldBe(BusinessErrors.SuspensionReasonRequired);
        business.Status.ShouldBe(BusinessStatus.Active);
    }

    [Fact]
    public void Suspend_RecordsWhoAndWhy()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);
        Guid adminId = Uuid7.New();

        business.Suspend("non-payment for 60 days", adminId, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        business.Status.ShouldBe(BusinessStatus.Suspended);
        business.SuspensionReason.ShouldBe("non-payment for 60 days");

        BusinessStatusChange latest = business.StatusHistory[^1];
        latest.ChangedByUserId.ShouldBe(adminId);
        latest.Reason.ShouldBe("non-payment for 60 days");
    }

    [Fact]
    public void Suspend_Twice_IsANormalRefusal()
    {
        // Two admins both clicking suspend is ordinary, so it is a Result,
        // not an exception.
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);
        business.Suspend("fraud", null, Now);

        business.Suspend("fraud", null, Now).Error.Code
            .ShouldBe("tenant.invalid_status_transition");
    }

    [Fact]
    public void Suspend_BeforeVerification_IsRefused()
    {
        Business business = NewRegistered(out _);

        business.Suspend("fraud", null, Now).Error.Code
            .ShouldBe("tenant.invalid_status_transition");
    }

    [Fact]
    public void Activate_OnlyWorksFromSuspended()
    {
        Business business = NewRegistered(out string hash);

        // Not a shortcut past email verification.
        business.Activate(null, Now).Error.Code.ShouldBe("tenant.invalid_status_transition");

        business.VerifyEmail(hash, Now);
        business.Activate(null, Now).Error.Code.ShouldBe("tenant.invalid_status_transition");
    }

    [Fact]
    public void Activate_ClearsTheSuspension_ButKeepsTheOriginalActivationDate()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);
        business.Suspend("fraud", null, Now.AddDays(1));

        business.Activate(null, Now.AddDays(2)).IsSuccess.ShouldBeTrue();

        business.Status.ShouldBe(BusinessStatus.Active);
        business.SuspensionReason.ShouldBeNull();
        business.SuspendedAt.ShouldBeNull();

        // The date the customer came on board, not the date they came back.
        business.ActivatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Archive_IsTerminal()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);

        business.Archive(null, Now.AddYears(1)).IsSuccess.ShouldBeTrue();
        business.Status.ShouldBe(BusinessStatus.Archived);

        // Nothing comes back from archived: bookings in another database
        // reference this tenant id with no foreign key to protect them.
        business.Archive(null, Now.AddYears(1)).IsFailure.ShouldBeTrue();
        business.Suspend("x", null, Now.AddYears(1)).IsFailure.ShouldBeTrue();
        business.Activate(null, Now.AddYears(1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void EveryTransition_AppendsToHistory()
    {
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);
        business.Suspend("fraud", null, Now.AddDays(1));
        business.Activate(null, Now.AddDays(2));
        business.Archive(null, Now.AddDays(3));

        business.StatusHistory.Count.ShouldBe(5);
        business.StatusHistory.Select(h => h.ToStatus).ShouldBe(
        [
            BusinessStatus.PendingVerification,
            BusinessStatus.Active,
            BusinessStatus.Suspended,
            BusinessStatus.Active,
            BusinessStatus.Archived,
        ]);
    }

    [Fact]
    public void EveryPublishedEvent_CarriesTheBusinessIdAsItsTenantId()
    {
        // This service owns the tenant root, so the business being changed IS
        // the tenant the event refers to. Downstream services key off it.
        Business business = NewRegistered(out string hash);
        business.VerifyEmail(hash, Now);

        business.DomainEvents.OfType<IPublishableEvent>()
            .ShouldAllBe(e => e.TenantId == business.Id);
    }
}
