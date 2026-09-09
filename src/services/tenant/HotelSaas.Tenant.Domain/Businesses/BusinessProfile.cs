using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.Tenant.Domain.Businesses;

// Address, contact and branding.
//
// Split from Business rather than folded into it because the two change on
// completely different rhythms: identity and status are touched by
// registration and by admins, while a profile is edited casually and often.
public sealed class BusinessProfile : Entity
{
    private BusinessProfile(Guid id, Guid businessId) : base(id) => BusinessId = businessId;

    private BusinessProfile()
    {
    }

    public Guid BusinessId { get; private set; }

    public string? LegalAddress { get; private set; }

    public string? City { get; private set; }

    public string? State { get; private set; }

    public string? PostalCode { get; private set; }

    public string? ContactPhone { get; private set; }

    public string? TaxIdentifier { get; private set; }

    public string? WebsiteUrl { get; private set; }

    public string? LogoUrl { get; private set; }

    // Created empty at registration. Asking for a full address before the
    // owner has even confirmed their email loses signups.
    public static BusinessProfile Empty(Guid businessId) => new(Uuid7.New(), businessId);

    public void Update(
        string? legalAddress,
        string? city,
        string? state,
        string? postalCode,
        string? contactPhone,
        string? taxIdentifier,
        string? websiteUrl,
        string? logoUrl)
    {
        LegalAddress = Normalise(legalAddress);
        City = Normalise(city);
        State = Normalise(state);
        PostalCode = Normalise(postalCode);
        ContactPhone = Normalise(contactPhone);
        TaxIdentifier = Normalise(taxIdentifier);
        WebsiteUrl = Normalise(websiteUrl);
        LogoUrl = Normalise(logoUrl);
    }

    // Empty string and null mean the same thing to a user, so they should
    // mean the same thing in the database too.
    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
