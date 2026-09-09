using System.Globalization;
using System.Text;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Tenant.Infrastructure.Persistence;

// Reads, projected straight to DTOs in SQL.
//
// No aggregates are loaded here (docs/05-code-structure.md 2.5). Rendering
// one row of an admin list does not need a Business with its full status
// history and every token it was ever issued.
internal sealed class BusinessQueries(TenantDbContext db) : IBusinessQueries
{
    public Task<BusinessDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
        => db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new BusinessDetail(
                b.Id,
                b.LegalName,
                b.DisplayName,
                b.OwnerEmail,
                b.OwnerName,
                b.Country,
                b.Status.ToString(),
                b.RegisteredAt,
                b.VerifiedAt,
                b.ActivatedAt,
                b.SuspendedAt,
                b.SuspensionReason,
                b.ArchivedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<BusinessProfileDto?> GetProfileAsync(Guid id, CancellationToken cancellationToken)
        => db.BusinessProfiles
            .AsNoTracking()
            .Where(p => p.BusinessId == id)
            .Select(p => new BusinessProfileDto(
                p.BusinessId,
                p.LegalAddress,
                p.City,
                p.State,
                p.PostalCode,
                p.ContactPhone,
                p.TaxIdentifier,
                p.WebsiteUrl,
                p.LogoUrl))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<BusinessSummary>> SearchAsync(
        BusinessSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        IQueryable<Business> query = db.Businesses.AsNoTracking();

        if (criteria.Status is { } status)
        {
            query = query.Where(b => b.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            string term = $"%{criteria.Query.Trim()}%";

            // ILike, not ToLower().Contains(): it maps to postgres ILIKE
            // rather than wrapping the column in a function on every row.
            query = query.Where(b =>
                EF.Functions.ILike(b.LegalName, term)
                || EF.Functions.ILike(b.DisplayName, term)
                || EF.Functions.ILike(b.OwnerEmail, term));
        }

        // Keyset pagination on the primary key.
        //
        // The id is uuid v7, so ordering by it is ordering by creation time
        // (ADR-0010) - no second column in the sort, and no offset to drift
        // under concurrent inserts (docs/00-conventions.md 5).
        if (DecodeCursor(criteria.Cursor) is { } afterId)
        {
            query = query.Where(b => b.Id.CompareTo(afterId) < 0);
        }

        // One extra row, purely to learn whether another page exists without
        // running a second COUNT over the whole table.
        List<BusinessSummary> rows = await query
            .OrderByDescending(b => b.Id)
            .Take(criteria.Limit + 1)
            .Select(b => new BusinessSummary(
                b.Id,
                b.LegalName,
                b.DisplayName,
                b.OwnerEmail,
                b.Country,
                b.Status.ToString(),
                b.RegisteredAt,
                b.ActivatedAt))
            .ToListAsync(cancellationToken);

        bool hasMore = rows.Count > criteria.Limit;

        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PagedResult<BusinessSummary>(
            rows,
            hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].Id) : null);
    }

    public async Task<PagedResult<StatusChangeDto>> GetStatusHistoryAsync(
        Guid businessId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<BusinessStatusChange> query = db.BusinessStatusChanges
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId);

        if (DecodeCursor(cursor) is { } afterId)
        {
            query = query.Where(c => c.Id.CompareTo(afterId) < 0);
        }

        List<StatusChangeDto> rows = await query
            .OrderByDescending(c => c.Id)
            .Take(limit + 1)
            .Select(c => new StatusChangeDto(
                c.Id,
                c.FromStatus == null ? null : c.FromStatus.ToString(),
                c.ToStatus.ToString(),
                c.Reason,
                c.ChangedByUserId,
                c.CreatedAt))
            .ToListAsync(cancellationToken);

        bool hasMore = rows.Count > limit;

        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PagedResult<StatusChangeDto>(
            rows,
            hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].Id) : null);
    }

    // Opaque to the client on purpose. A cursor is a position, not an API:
    // base64 signals "pass this back unchanged" rather than inviting anyone
    // to construct one.
    private static string EncodeCursor(Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.ToString("D", CultureInfo.InvariantCulture)));

    private static Guid? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        // A malformed cursor returns the first page rather than an error.
        // It is a position hint, and a stale bookmark should not be a 400.
        try
        {
            string raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return Guid.TryParse(raw, out Guid id) ? id : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
