using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.BuildingBlocks.Tests;

// ADR-0004 layer 2, tested at the EF level.
//
// TestDbContext configures no filtering of its own. Everything asserted
// here comes from HotelSaasDbContext, which is the point: a service gets
// this protection from the base class and the ITenantScoped interface, not
// from remembering to write a Where clause.
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ATenantCannotSeeAnotherTenantsRows()
    {
        Guid tenantA = Uuid7.New();
        Guid tenantB = Uuid7.New();

        await using (TestDbContext asA = postgres.CreateContext(new MutableTenantContext(tenantA)))
        {
            asA.Widgets.Add(new Widget(Uuid7.New(), "belongs-to-a"));
            await asA.SaveChangesAsync();
        }

        await using (TestDbContext asB = postgres.CreateContext(new MutableTenantContext(tenantB)))
        {
            asB.Widgets.Add(new Widget(Uuid7.New(), "belongs-to-b"));
            await asB.SaveChangesAsync();
        }

        await using TestDbContext readAsA = postgres.CreateContext(new MutableTenantContext(tenantA));

        List<string> visible = await readAsA.Widgets.Select(w => w.Name).ToListAsync();

        visible.ShouldContain("belongs-to-a");
        visible.ShouldNotContain("belongs-to-b");
    }

    [Fact]
    public async Task TheFilterFollowsTheContextInstance_NotTheCachedModel()
    {
        // The subtle failure this guards against: EF caches the model per
        // context TYPE. If the tenant value were baked into the compiled
        // query filter when the model was first built, every later context
        // would silently read the FIRST context's tenant - a cross-tenant
        // leak that no amount of correct calling code would prevent.
        Guid tenantA = Uuid7.New();
        Guid tenantB = Uuid7.New();

        await using (TestDbContext seed = postgres.CreateContext(new MutableTenantContext(tenantA)))
        {
            seed.Widgets.Add(new Widget(Uuid7.New(), "instance-check-a"));
            await seed.SaveChangesAsync();
        }

        await using (TestDbContext seed = postgres.CreateContext(new MutableTenantContext(tenantB)))
        {
            seed.Widgets.Add(new Widget(Uuid7.New(), "instance-check-b"));
            await seed.SaveChangesAsync();
        }

        // Two different instances, in this order, of the same context type.
        await using TestDbContext first = postgres.CreateContext(new MutableTenantContext(tenantA));
        (await first.Widgets.CountAsync(w => w.Name == "instance-check-a")).ShouldBe(1);

        await using TestDbContext second = postgres.CreateContext(new MutableTenantContext(tenantB));
        (await second.Widgets.CountAsync(w => w.Name == "instance-check-b")).ShouldBe(1);
        (await second.Widgets.CountAsync(w => w.Name == "instance-check-a")).ShouldBe(0);
    }

    [Fact]
    public async Task WithNoTenantInContext_TheFilterMatchesNothing()
    {
        // Fail closed. The filter is strict - there is deliberately no
        // "or the tenant is null" escape hatch, because that expression is
        // one typo away from returning every tenant's data.
        Guid tenantId = Uuid7.New();

        await using (TestDbContext seed = postgres.CreateContext(new MutableTenantContext(tenantId)))
        {
            seed.Widgets.Add(new Widget(Uuid7.New(), "fail-closed"));
            await seed.SaveChangesAsync();
        }

        await using TestDbContext platform = postgres.CreateContext(new MutableTenantContext(null));

        (await platform.Widgets.CountAsync(w => w.Name == "fail-closed")).ShouldBe(0);
    }

    [Fact]
    public async Task PlatformScopeMustAskForCrossTenantReadsOutLoud()
    {
        // IgnoreQueryFilters is greppable and auditable, which is the whole
        // reason platform access is an explicit call rather than a flag.
        Guid tenantId = Uuid7.New();

        await using (TestDbContext seed = postgres.CreateContext(new MutableTenantContext(tenantId)))
        {
            seed.Widgets.Add(new Widget(Uuid7.New(), "platform-visible"));
            await seed.SaveChangesAsync();
        }

        await using TestDbContext platform = postgres.CreateContext(new MutableTenantContext(null));

        (await platform.Widgets.IgnoreQueryFilters()
            .CountAsync(w => w.Name == "platform-visible")).ShouldBe(1);
    }

    [Fact]
    public async Task TenantIdIsStampedOnSave_NotSuppliedByTheCaller()
    {
        // The handler never sets tenant_id. A handler that could assign a
        // tenant is one bug away from writing into the wrong one, and a bad
        // write is worse than a bad read: it is undetectable afterwards.
        Guid tenantId = Uuid7.New();
        Guid widgetId = Uuid7.New();

        await using (TestDbContext write = postgres.CreateContext(new MutableTenantContext(tenantId)))
        {
            write.Widgets.Add(new Widget(widgetId, "stamped"));
            await write.SaveChangesAsync();
        }

        await using TestDbContext read = postgres.CreateContext(new MutableTenantContext(tenantId));

        Widget stored = await read.Widgets.SingleAsync(w => w.Id == widgetId);
        stored.TenantId.ShouldBe(tenantId);
    }

    [Fact]
    public async Task WritingIntoAnotherTenantIsRefused()
    {
        Guid actingTenant = Uuid7.New();
        Guid victimTenant = Uuid7.New();

        await using TestDbContext write = postgres.CreateContext(new MutableTenantContext(actingTenant));

        Widget smuggled = new(Uuid7.New(), "smuggled") { TenantId = victimTenant };
        write.Widgets.Add(smuggled);

        InvalidOperationException thrown =
            await Should.ThrowAsync<InvalidOperationException>(() => write.SaveChangesAsync());

        thrown.Message.ShouldContain("Refusing to write");
    }

    [Fact]
    public async Task AuditTimestampsAreStampedAutomatically()
    {
        Guid tenantId = Uuid7.New();
        Guid widgetId = Uuid7.New();
        FixedClock clock = new(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));

        await using (TestDbContext write = postgres.CreateContext(new MutableTenantContext(tenantId), clock))
        {
            write.Widgets.Add(new Widget(widgetId, "timestamped"));
            await write.SaveChangesAsync();
        }

        clock.UtcNow = clock.UtcNow.AddHours(3);

        await using (TestDbContext update = postgres.CreateContext(new MutableTenantContext(tenantId), clock))
        {
            Widget widget = await update.Widgets.SingleAsync(w => w.Id == widgetId);
            widget.Name = "renamed";
            await update.SaveChangesAsync();
        }

        await using TestDbContext read = postgres.CreateContext(new MutableTenantContext(tenantId), clock);
        Widget stored = await read.Widgets.SingleAsync(w => w.Id == widgetId);

        // CreatedAt must survive the update: it is a historical fact.
        stored.CreatedAt.ShouldBe(new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
        stored.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 9, 8, 13, 0, 0, TimeSpan.Zero));
    }
}
