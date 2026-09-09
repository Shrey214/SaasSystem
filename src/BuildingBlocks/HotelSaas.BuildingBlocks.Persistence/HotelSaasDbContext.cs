using System.Linq.Expressions;
using System.Reflection;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Persistence.Errors;
using HotelSaas.BuildingBlocks.Persistence.Idempotency;
using HotelSaas.BuildingBlocks.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HotelSaas.BuildingBlocks.Persistence;

// Base DbContext for every service.
//
// Carries the three things all 14 share: the tenant query filter and stamp
// (ADR-0004 layer 2), the audit timestamps, and the outbox / idempotency /
// error_logs tables.
public abstract class HotelSaasDbContext(
    DbContextOptions options,
    ITenantContext tenantContext,
    IClock clock,
    ICorrelationContext correlationContext)
    : DbContext(options), IUnitOfWork
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(HotelSaasDbContext).GetMethod(
            nameof(ApplyTenantFilter),
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ApplyTenantFilter not found.");

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<ErrorLogEntry> ErrorLogs => Set<ErrorLogEntry>();

    // Read by the query filter below. EF Core turns this into a query
    // parameter, evaluated per query rather than baked into the model.
    public Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HotelSaasDbContext).Assembly);

        DeclareEntityKeysAsApplicationGenerated(modelBuilder);
        ApplyTenantFilters(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money is numeric(18,4) everywhere, never float
        // (docs/00-conventions.md 3). Set once here so no entity can forget.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);

        base.ConfigureConventions(configurationBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        StampAuditTimestamps();

        // Last, so events raised by anything above are included, and inside
        // the SAME transaction as the business change (ADR-0006). Publishing
        // from a handler instead would announce things that later roll back.
        DrainDomainEventsToOutbox();

        return base.SaveChangesAsync(cancellationToken);
    }

    private void DrainDomainEventsToOutbox()
    {
        List<AggregateRoot> roots = ChangeTracker
            .Entries<AggregateRoot>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .Select(entry => entry.Entity)
            .ToList();

        foreach (AggregateRoot root in roots)
        {
            foreach (IDomainEvent domainEvent in root.DomainEvents)
            {
                // Only events marked publishable leave the service. The rest
                // are internal and stay in memory.
                if (domainEvent is not IPublishableEvent publishable)
                {
                    continue;
                }

                OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = publishable.EventId,
                    TenantId = publishable.TenantId ?? CurrentTenantId,
                    AggregateType = publishable.AggregateType,
                    AggregateId = publishable.AggregateId,
                    EventType = publishable.EventType,
                    Payload = Messaging.EventSerialization.Serialise(domainEvent, domainEvent.GetType()),
                    CorrelationId = correlationContext.CorrelationId,
                    OccurredAt = publishable.OccurredAt,
                });
            }

            root.ClearDomainEvents();
        }
    }

    // ADR-0004: tenant_id is stamped here, never set by a handler. A handler
    // that can assign a tenant is one bug away from writing into the wrong
    // one, and a wrong write is worse than a wrong read - it is undetectable
    // afterwards.
    private void StampTenant()
    {
        Guid? current = CurrentTenantId;

        foreach (EntityEntry entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantScoped scoped)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (scoped.TenantId == Guid.Empty)
                    {
                        scoped.TenantId = current
                            ?? throw new InvalidOperationException(
                                $"Cannot save {entry.Entity.GetType().Name}: the request is " +
                                "platform-scoped, so there is no tenant to stamp.");
                    }
                    else if (current is not null && scoped.TenantId != current)
                    {
                        throw new InvalidOperationException(
                            $"Refusing to write {entry.Entity.GetType().Name} for tenant " +
                            $"{scoped.TenantId} from a request scoped to tenant {current}.");
                    }

                    break;

                case EntityState.Modified:
                    // tenant_id is immutable. Changing it would move a row
                    // between customers.
                    PropertyEntry tenantProperty = entry.Property(nameof(ITenantScoped.TenantId));
                    if (tenantProperty.IsModified)
                    {
                        throw new InvalidOperationException(
                            $"tenant_id on {entry.Entity.GetType().Name} cannot be changed.");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private void StampAuditTimestamps()
    {
        DateTimeOffset now = clock.UtcNow;

        foreach (EntityEntry entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not IAuditable auditable)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                auditable.CreatedAt = now;
                auditable.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                auditable.UpdatedAt = now;
                entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
            }
        }
    }

    // Every Entity.Id is generated by US, never by the database (ADR-0010).
    //
    // This is not cosmetic. EF conventionally treats a Guid key as
    // ValueGeneratedOnAdd, and when it discovers an UNTRACKED entity hanging
    // off a tracked aggregate it decides Added-vs-Modified by asking whether
    // the key is already set. Ours always is - the constructor mints a uuid
    // v7 so the aggregate can raise a domain event carrying its own id - so
    // EF concludes the row must already exist and issues an UPDATE.
    //
    // The UPDATE matches nothing and throws DbUpdateConcurrencyException:
    // "expected to affect 1 row(s), but actually affected 0 row(s)". It only
    // shows up when a child is added to an ALREADY-LOADED aggregate; a whole
    // graph passed to Add() works fine, so the bug hides until the second
    // use case that mutates an existing aggregate.
    //
    // Declaring the keys application-generated tells EF the truth and it
    // inserts correctly. Applied here so all 14 services get it, rather than
    // 14 services each hitting the same afternoon of confusion.
    //
    // bigint identity tables (outbox, error_logs, idempotency_keys) do NOT
    // derive from Entity, so they keep real database generation.
    private static void DeclareEntityKeysAsApplicationGenerated(ModelBuilder modelBuilder)
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType
            in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            entityType
                .FindProperty(nameof(Entity.Id))
                ?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType
            in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned() || !typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            ApplyTenantFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, [modelBuilder]);
        }
    }

    // Strict on purpose: no "or the tenant is null" escape hatch.
    //
    // A filter like `TenantId == current || current == null` would let a
    // platform-scoped request read every tenant implicitly, and that
    // expression is one typo away from doing it for tenant requests too
    // (ADR-0004 layer 1). Platform-wide reads must ask for it out loud with
    // IgnoreQueryFilters(), which is greppable and auditable.
    //
    // The consequence: with no tenant in context the filter matches nothing.
    // That is fail-closed, which is the correct direction to fail.
    protected void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = entity => entity.TenantId == CurrentTenantId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}
