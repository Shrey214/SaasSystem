using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Tenant.IntegrationTests;

// The mandatory per-service isolation test (ADR-0004), against HTTP.
//
// This service is the special case: `businesses` deliberately has no
// tenant_id and no query filter, because a business row DEFINES a tenant
// rather than belonging to one. So the isolation that must be proven here is
// different - it is that the /me endpoints are scoped by the caller's own
// tenant and cannot be pointed at somebody else's business.
[Collection(TenantApiCollection.Name)]
public sealed class TenantScopingTests(TenantApiFactory factory)
{
    [Fact]
    public async Task MeEndpoints_ReturnTheCallersOwnBusinessOnly()
    {
        using HttpClient client = factory.NewClient();

        Guid tenantA = await TenantApiFactory.RegisterAndActivateAsync(client);
        Guid tenantB = await TenantApiFactory.RegisterAndActivateAsync(client);

        using HttpClient asA = factory.NewClient();
        asA.DefaultRequestHeaders.Add("X-Tenant-Id", tenantA.ToString());

        HttpResponseMessage response = await asA.GetAsync("/api/v1/businesses/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid returned = body.RootElement.GetProperty("id").GetGuid();

        returned.ShouldBe(tenantA);
        returned.ShouldNotBe(tenantB);
    }

    [Fact]
    public async Task MeProfileUpdate_CannotBeAimedAtAnotherTenant()
    {
        // There is no business id in the route or the body of
        // PUT /me/profile, so there is nothing to tamper with. This asserts
        // the write actually lands on the caller's own row.
        using HttpClient client = factory.NewClient();

        Guid tenantA = await TenantApiFactory.RegisterAndActivateAsync(client);
        Guid tenantB = await TenantApiFactory.RegisterAndActivateAsync(client);

        using HttpClient asA = factory.NewClient();
        asA.DefaultRequestHeaders.Add("X-Tenant-Id", tenantA.ToString());

        HttpResponseMessage update = await asA.PutAsJsonAsync(
            "/api/v1/businesses/me/profile",
            new { city = "Ujjain", state = "Madhya Pradesh" });

        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using IServiceScope scope = factory.Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        (await db.BusinessProfiles.SingleAsync(p => p.BusinessId == tenantA)).City.ShouldBe("Ujjain");
        (await db.BusinessProfiles.SingleAsync(p => p.BusinessId == tenantB)).City.ShouldBeNull();
    }

    [Fact]
    public async Task MeStatusHistory_ShowsOnlyTheCallersOwnEntries()
    {
        using HttpClient client = factory.NewClient();

        Guid tenantA = await TenantApiFactory.RegisterAndActivateAsync(client);
        Guid tenantB = await TenantApiFactory.RegisterAndActivateAsync(client);

        await client.PostAsJsonAsync(
            $"/api/v1/platform/businesses/{tenantB}/suspend",
            new { reason = "tenant B specific suspension" });

        using HttpClient asA = factory.NewClient();
        asA.DefaultRequestHeaders.Add("X-Tenant-Id", tenantA.ToString());

        HttpResponseMessage response = await asA.GetAsync("/api/v1/businesses/me/status-history");
        string json = await response.Content.ReadAsStringAsync();

        json.ShouldNotContain("tenant B specific suspension");
    }

    [Fact]
    public async Task MeEndpoints_WithNoTenantAtAll_Fail()
    {
        // Fail closed. RequireTenantId throws rather than returning
        // everything, which is the correct direction to fail.
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/businesses/me");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await BusinessRegistrationTests.ReadCodeAsync(response)).ShouldBe("internal_error");
    }
}
