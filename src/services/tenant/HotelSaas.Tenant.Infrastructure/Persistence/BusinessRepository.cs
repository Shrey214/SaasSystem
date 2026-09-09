using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Tenant.Infrastructure.Persistence;

// Loads and saves whole aggregates. Commands only.
internal sealed class BusinessRepository(TenantDbContext db) : IBusinessRepository
{
    public Task<Business?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => LoadFullAggregate().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<Business?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        string normalised = Normalise(email);
        return LoadFullAggregate().FirstOrDefaultAsync(b => b.OwnerEmail == normalised, cancellationToken);
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        string normalised = Normalise(email);
        return db.Businesses.AnyAsync(b => b.OwnerEmail == normalised, cancellationToken);
    }

    public void Add(Business business) => db.Businesses.Add(business);

    // The children come with it, always.
    //
    // An aggregate must be loaded whole: VerifyEmail cannot check a token it
    // was not given, and Suspend cannot append to a history it cannot see.
    // Loading part of an aggregate and calling a method on it is how
    // invariants get silently skipped.
    private IQueryable<Business> LoadFullAggregate()
        => db.Businesses
            .Include(b => b.Profile)
            .Include(b => b.StatusHistory)
            .Include(b => b.EmailVerifications)

            // Split query, because EF warned about this one: two collection
            // includes in a single statement is a cartesian product, so 20
            // status rows x 5 tokens returns 100 rows to build 25 objects.
            // Three round trips beats a result set that grows by
            // multiplication.
            .AsSplitQuery();

    // Must match Business.Register exactly, or a lookup will miss a row that
    // the unique index would still refuse.
    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
