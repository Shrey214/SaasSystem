using HotelSaas.BuildingBlocks.Application;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Application.Abstractions;

// What the use cases need from the outside world.
//
// Interfaces live here, implementations in Infrastructure. That inversion
// is what lets a handler be tested without a database, and what stops
// Application from referencing EF Core.

// Commands: load an aggregate, call a method on it, save.
public interface IBusinessRepository
{
    // Includes the profile, status history and verification tokens, because
    // the aggregate enforces its rules across all of them - VerifyEmail
    // cannot check a token it was not given.
    Task<Business?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Business?> GetByEmailAsync(string email, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    void Add(Business business);
}

// Queries: project straight to a DTO in SQL, never through the aggregate
// (docs/05-code-structure.md 2.5). Loading a Business with its full history
// to render one row of an admin list would be absurd.
public interface IBusinessQueries
{
    Task<BusinessDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken);

    Task<BusinessProfileDto?> GetProfileAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<BusinessSummary>> SearchAsync(
        BusinessSearchCriteria criteria,
        CancellationToken cancellationToken);

    Task<PagedResult<StatusChangeDto>> GetStatusHistoryAsync(
        Guid businessId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);
}

// Generates verification tokens and hashes them.
//
// Only the hash is ever stored: a database dump of plain tokens would be a
// set of working account-takeover links.
public interface IVerificationTokenGenerator
{
    // Returns the token to email, and the hash to store. The caller never
    // sees a way to turn a stored hash back into a link.
    (string Token, string Hash) Generate();

    string Hash(string token);
}

// Records that a verification link needs sending.
//
// At stage 4 there is no email service, so this writes to the log and the
// response in Development. Stage 16 replaces the implementation with a real
// notification event and nothing else changes.
public interface IVerificationDispatcher
{
    // True only in Development, where the token is echoed in the response
    // so the flow can be walked end to end without an inbox. Never true
    // anywhere else - a token in an API response is a token in a log.
    bool ExposesTokens { get; }

    Task DispatchAsync(
        Guid businessId,
        string ownerEmail,
        string token,
        CancellationToken cancellationToken);
}

public sealed record BusinessSummary(
    Guid Id,
    string LegalName,
    string DisplayName,
    string OwnerEmail,
    string Country,
    string Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? ActivatedAt);

public sealed record BusinessDetail(
    Guid Id,
    string LegalName,
    string DisplayName,
    string OwnerEmail,
    string OwnerName,
    string Country,
    string Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? SuspendedAt,
    string? SuspensionReason,
    DateTimeOffset? ArchivedAt);

public sealed record BusinessProfileDto(
    Guid BusinessId,
    string? LegalAddress,
    string? City,
    string? State,
    string? PostalCode,
    string? ContactPhone,
    string? TaxIdentifier,
    string? WebsiteUrl,
    string? LogoUrl);

public sealed record StatusChangeDto(
    Guid Id,
    string? FromStatus,
    string ToStatus,
    string Reason,
    Guid? ChangedByUserId,
    DateTimeOffset ChangedAt);

public sealed record BusinessSearchCriteria(
    string? Query,
    BusinessStatus? Status,
    string? Cursor,
    int Limit);
