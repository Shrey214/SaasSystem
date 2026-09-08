using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.BuildingBlocks.Tests.TestSupport;

// A tenant-scoped entity, exactly as a real service would declare one.
internal sealed class Widget : Entity, ITenantScoped
{
    public Widget(Guid id, string name) : base(id) => Name = name;

    private Widget()
    {
    }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}

// Stands in for a service DbContext. Nothing here configures tenant
// filtering or audit stamping - that is the point. It comes from the base
// class, which is what the tests are checking.
internal sealed class TestDbContext(
    DbContextOptions<TestDbContext> options,
    ITenantContext tenantContext,
    IClock clock) : HotelSaasDbContext(options, tenantContext, clock)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Widget>(builder =>
        {
            builder.ToTable("widgets");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).HasMaxLength(200).IsRequired();

            // tenant_id first, for the query plan (ADR-0004).
            builder.HasIndex(x => new { x.TenantId, x.Name });
        });
    }
}
