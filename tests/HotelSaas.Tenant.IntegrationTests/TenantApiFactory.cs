using System.Net.Http.Json;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace HotelSaas.Tenant.IntegrationTests;

// The real API, over real HTTP, against a real PostgreSQL.
//
// Not a mocked pipeline: the point is to exercise middleware order, model
// binding, the problem-details shape and the actual SQL. Several of the
// stage-4 bugs (a required int query parameter, EF issuing UPDATE for a new
// child row) were invisible to unit tests and obvious here.
public sealed class TenantApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("hs_tenant")
        .WithUsername("hs_tenant_user")
        .WithPassword("integration_test_password")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Force the host to build and migrate before the first test runs.
        using IServiceScope scope = Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Development, so the header tenant stub and the dev-only
        // verification token in the response are both available. Stage 5
        // replaces this with a signed token and the stub disappears.
        builder.UseEnvironment(Environments.Development);

        builder.UseSetting("ConnectionStrings:TenantDb", _postgres.GetConnectionString());
    }

    public HttpClient NewClient() => CreateClient();

    // Registers a business and walks it all the way to Active, which is what
    // most tests actually need as a starting point.
    public static async Task<Guid> RegisterAndActivateAsync(HttpClient client, string? email = null)
    {
        RegisterResponse registered = await RegisterAsync(client, email);

        HttpResponseMessage verify = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = registered.VerificationToken });

        verify.EnsureSuccessStatusCode();
        return registered.BusinessId;
    }

    public static async Task<RegisterResponse> RegisterAsync(HttpClient client, string? email = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/businesses", new
        {
            legalName = "Test Hotels Pvt Ltd",
            displayName = "Test Hotels",
            ownerEmail = email ?? UniqueEmail(),
            ownerName = "Test Owner",
            country = "IN",
        });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
    }

    public static string UniqueEmail() => $"owner+{Guid.NewGuid():N}@test.dev";
}

public sealed record RegisterResponse(Guid BusinessId, string Status, string? VerificationToken);

[CollectionDefinition(Name)]
public sealed class TenantApiCollection : ICollectionFixture<TenantApiFactory>
{
    public const string Name = "tenant-api";
}
