using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Persistence;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Tenant.Infrastructure.Persistence;

public sealed class TenantDbContext(
    DbContextOptions<TenantDbContext> options,
    ITenantContext tenantContext,
    IClock clock,
    ICorrelationContext correlationContext)
    : HotelSaasDbContext(options, tenantContext, clock, correlationContext)
{
    public DbSet<Business> Businesses => Set<Business>();

    public DbSet<BusinessProfile> BusinessProfiles => Set<BusinessProfile>();

    public DbSet<BusinessStatusChange> BusinessStatusChanges => Set<BusinessStatusChange>();

    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Base first: it brings outbox_messages, idempotency_keys,
        // processed_messages and error_logs, and applies the tenant filter
        // to anything implementing ITenantScoped.
        base.OnModelCreating(modelBuilder);

        // Note what is absent: no tenant filter is applied to any table in
        // this service, because Business does not implement ITenantScoped.
        // Its id IS the tenant id. This is the one service where that is
        // true (docs/01-bounded-contexts.md 2.5).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenantDbContext).Assembly);
    }
}
