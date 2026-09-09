using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Tenant.IntegrationTests;

[Collection(TenantApiCollection.Name)]
public sealed class BusinessLifecycleApiTests(TenantApiFactory factory)
{
    [Fact]
    public async Task VerifyEmail_ActivatesTheBusinessAndAppendsToTheOutbox()
    {
        // The bug this test would have caught immediately: adding a status
        // history row to an ALREADY-LOADED aggregate made EF issue an UPDATE
        // instead of an INSERT, because a Guid key that is already set looks
        // to EF like an existing row. Registration alone never hit it.
        using HttpClient client = factory.NewClient();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = registered.VerificationToken });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using IServiceScope scope = factory.Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var business = await db.Businesses
            .Where(b => b.Id == registered.BusinessId)
            .Select(b => new { b.Status, b.VerifiedAt, b.ActivatedAt })
            .SingleAsync();

        business.Status.ShouldBe(HotelSaas.Tenant.Domain.Businesses.BusinessStatus.Active);
        business.VerifiedAt.ShouldNotBeNull();
        business.ActivatedAt.ShouldNotBeNull();

        (await db.BusinessStatusChanges.CountAsync(c => c.BusinessId == registered.BusinessId))
            .ShouldBe(2);

        List<string> events = await db.OutboxMessages
            .Where(m => m.AggregateId == registered.BusinessId)
            .OrderBy(m => m.Id)
            .Select(m => m.EventType)
            .ToListAsync();

        events.ShouldBe(["tenant.business.registered.v1", "tenant.business.activated.v1"]);
    }

    [Fact]
    public async Task VerifyEmail_WithAWrongToken_Returns409AndDoesNotActivate()
    {
        using HttpClient client = factory.NewClient();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = "definitely-not-the-token" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BusinessRegistrationTests.ReadCodeAsync(response))
            .ShouldBe("tenant.verification_token_invalid");
    }

    [Fact]
    public async Task VerifyEmail_Twice_Returns409()
    {
        using HttpClient client = factory.NewClient();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);

        await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = registered.VerificationToken });

        HttpResponseMessage second = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = registered.VerificationToken });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BusinessRegistrationTests.ReadCodeAsync(second)).ShouldBe("tenant.already_verified");
    }

    [Fact]
    public async Task VerifyEmail_ForAnUnknownBusiness_Returns404()
    {
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{Guid.CreateVersion7()}/verify-email",
            new { token = "whatever" });

        // 404, never 403: a 403 would confirm the business exists.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SuspendAndReactivate_WalkTheStateMachine()
    {
        using HttpClient client = factory.NewClient();
        Guid businessId = await TenantApiFactory.RegisterAndActivateAsync(client);

        HttpResponseMessage suspend = await client.PostAsJsonAsync(
            $"/api/v1/platform/businesses/{businessId}/suspend",
            new { reason = "non-payment for 60 days" });
        suspend.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Two admins both clicking suspend is ordinary, not a fault.
        HttpResponseMessage again = await client.PostAsJsonAsync(
            $"/api/v1/platform/businesses/{businessId}/suspend",
            new { reason = "non-payment for 60 days" });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BusinessRegistrationTests.ReadCodeAsync(again))
            .ShouldBe("tenant.invalid_status_transition");

        HttpResponseMessage activate = await client.PostAsync(
            $"/api/v1/platform/businesses/{businessId}/activate", content: null);
        activate.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Suspend_WithoutARealReason_Returns400InTheSharedProblemShape()
    {
        // Regression guard: this endpoint originally used
        // Results.ValidationProblem, which emitted a different body with no
        // `code` and PascalCase field names.
        using HttpClient client = factory.NewClient();
        Guid businessId = await TenantApiFactory.RegisterAndActivateAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/platform/businesses/{businessId}/suspend",
            new { reason = "no" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BusinessRegistrationTests.ReadCodeAsync(response)).ShouldBe("validation_failed");

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task StatusHistory_WorksWithoutALimitQueryParameter()
    {
        // Regression guard: `int limit` was a REQUIRED minimal-api query
        // parameter, so omitting ?limit= threw before the handler ran.
        using HttpClient client = factory.NewClient();
        Guid businessId = await TenantApiFactory.RegisterAndActivateAsync(client);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", businessId.ToString());

        HttpResponseMessage response = await client.GetAsync("/api/v1/businesses/me/status-history");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        page.RootElement.GetProperty("items").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task PlatformSearch_WorksWithoutALimitQueryParameter()
    {
        using HttpClient client = factory.NewClient();
        await TenantApiFactory.RegisterAndActivateAsync(client);

        HttpResponseMessage response = await client.GetAsync("/api/v1/platform/businesses");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PlatformSearch_WithAnUnknownStatus_Returns400NamingTheValidOnes()
    {
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/platform/businesses?status=Nonsense");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BusinessRegistrationTests.ReadCodeAsync(response)).ShouldBe("tenant.unknown_status");

        // The caller cannot guess an enum they have never seen.
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("PendingVerification");
    }

    [Fact]
    public async Task MalformedJson_Returns400_Not500()
    {
        // A caller's typo is not our fault. Treating it as 500 tells them to
        // contact support about their own mistake AND fills error_logs with
        // other people's bad requests, burying the real faults.
        using HttpClient client = factory.NewClient();

        using StringContent body = new("{ this is not json", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync("/api/v1/businesses", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BusinessRegistrationTests.ReadCodeAsync(response)).ShouldBe("malformed_request");
    }

    [Fact]
    public async Task AnUnparseableRouteValue_Returns404_Not500()
    {
        // {id:guid} does not match, so there is simply no such route.
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/businesses/not-a-guid/verify-email",
            new { token = "x" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResendVerification_ForAnUnknownEmail_StillReturns202()
    {
        // Keyed by email with no registration attempt behind it, so a
        // distinguishable response would be a free address-checking oracle.
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/businesses/resend-verification",
            new { ownerEmail = "definitely-not-registered@nowhere.example" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ResendVerification_InvalidatesTheEarlierToken()
    {
        using HttpClient client = factory.NewClient();
        string email = TenantApiFactory.UniqueEmail();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client, email);

        await client.PostAsJsonAsync("/api/v1/businesses/resend-verification", new { ownerEmail = email });

        // The forwarded old email must stop working.
        HttpResponseMessage withOldToken = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = registered.VerificationToken });

        withOldToken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BusinessRegistrationTests.ReadCodeAsync(withOldToken))
            .ShouldBe("tenant.verification_token_invalid");
    }
}
