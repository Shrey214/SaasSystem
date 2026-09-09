using HotelSaas.BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace HotelSaas.BuildingBlocks.Tests.TestSupport;

// A real PostgreSQL, started for the test run and thrown away after.
//
// Not the EF in-memory provider: it does not enforce unique constraints,
// check constraints or real transaction semantics, and constraints are
// exactly where the interesting bugs in this system get caught.
public sealed class PostgresFixture : IAsyncLifetime
{
    // postgres:17 - the same major version as infra/docker, and already
    // pulled locally by stage 2, so the container starts immediately.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("hs_test")
        .WithUsername("hs_test_user")
        .WithPassword("hs_test_password")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Create the schema once, from the same model the services use.
        await using TestDbContext context = CreateContext(new MutableTenantContext());
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    internal TestDbContext CreateContext(ITenantContext tenantContext, IClock? clock = null)
    {
        DbContextOptions<TestDbContext> options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new TestDbContext(
            options,
            tenantContext,
            clock ?? new FixedClock(DateTimeOffset.UtcNow),
            new FixedCorrelationContext());
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
