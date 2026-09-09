namespace HotelSaas.Tenant.Domain.Businesses;

// The business lifecycle.
//
// Self-service: registration goes straight to ACTIVE once the email is
// verified, with no human approval step. Goal/Domain.txt Part 1 lists
// "Business Activation | Platform Admin", and its business rule column
// reads "suspended businesses cannot use platform" - so that row is about
// UN-suspending, not about vetting new signups.
public enum BusinessStatus
{
    // Registered, email not yet confirmed. Cannot log in.
    PendingVerification = 0,

    // Verified and operating.
    Active = 1,

    // Stopped by a platform admin: fraud, non-payment, abuse. Reversible.
    Suspended = 2,

    // Terminal. Never deleted, because Goal/Domain.txt Part 1 requires
    // historical bookings to stay intact.
    Archived = 3,
}
