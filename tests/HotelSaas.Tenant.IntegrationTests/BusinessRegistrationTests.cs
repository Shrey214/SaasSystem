using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Tenant.IntegrationTests;

[Collection(TenantApiCollection.Name)]
public sealed class BusinessRegistrationTests(TenantApiFactory factory)
{
    [Fact]
    public async Task Register_Returns201WithLocation()
    {
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/businesses", new
        {
            legalName = "Porwal Hotels Pvt Ltd",
            displayName = "Porwal Hotels",
            ownerEmail = TenantApiFactory.UniqueEmail(),
            ownerName = "Shreyash Porwal",
            country = "IN",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();

        RegisterResponse? body = await response.Content.ReadFromJsonAsync<RegisterResponse>();
        body.ShouldNotBeNull();
        body.Status.ShouldBe("PendingVerification");
    }

    [Fact]
    public async Task Register_WritesTheBusinessAndItsOutboxRowInOneTransaction()
    {
        // ADR-0006: publishing from a handler instead would announce a
        // business that a rollback then un-created.
        using HttpClient client = factory.NewClient();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);

        using IServiceScope scope = factory.Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        (await db.Businesses.CountAsync(b => b.Id == registered.BusinessId)).ShouldBe(1);

        var outbox = await db.OutboxMessages
            .Where(m => m.AggregateId == registered.BusinessId)
            .Select(m => new { m.EventType, m.PublishedAt, m.TenantId, m.Payload })
            .ToListAsync();

        outbox.ShouldHaveSingleItem();
        outbox[0].EventType.ShouldBe("tenant.business.registered.v1");

        // Unpublished, and that is correct: there is no broker until stage 7.
        // Rows accumulate and users keep working.
        outbox[0].PublishedAt.ShouldBeNull();

        // The tenant id on the event is the business id - this service owns
        // the tenant root.
        outbox[0].TenantId.ShouldBe(registered.BusinessId);

        // The payload must carry facts, not just an id, so subscription and
        // reporting never call back synchronously.
        using JsonDocument payload = JsonDocument.Parse(outbox[0].Payload);
        payload.RootElement.GetProperty("legalName").GetString().ShouldNotBeNullOrWhiteSpace();
        payload.RootElement.GetProperty("country").GetString().ShouldBe("IN");
    }

    [Fact]
    public async Task Register_StoresOnlyAHashOfTheVerificationToken()
    {
        using HttpClient client = factory.NewClient();
        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);

        registered.VerificationToken.ShouldNotBeNullOrWhiteSpace();

        using IServiceScope scope = factory.Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        string storedHash = await db.EmailVerifications
            .Where(v => v.BusinessId == registered.BusinessId)
            .Select(v => v.TokenHash)
            .SingleAsync();

        // A database dump must not be a set of working account-takeover
        // links.
        storedHash.ShouldNotBe(registered.VerificationToken);
        storedHash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task Register_WithADuplicateEmail_Returns409()
    {
        using HttpClient client = factory.NewClient();
        string email = TenantApiFactory.UniqueEmail();

        await TenantApiFactory.RegisterAsync(client, email);

        HttpResponseMessage second = await client.PostAsJsonAsync("/api/v1/businesses", new
        {
            legalName = "Copycat Hotels",
            ownerEmail = email,
            ownerName = "Someone Else",
            country = "IN",
        });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(second)).ShouldBe("tenant.email_already_registered");
    }

    [Fact]
    public async Task Register_WithADuplicateEmailInDifferentCase_StillReturns409()
    {
        // Owner@x.com and owner@x.com are the same mailbox to every mail
        // server, so they must not become two businesses. This works only
        // because the column is normalised on write.
        using HttpClient client = factory.NewClient();
        string email = TenantApiFactory.UniqueEmail();

        await TenantApiFactory.RegisterAsync(client, email);

        HttpResponseMessage second = await client.PostAsJsonAsync("/api/v1/businesses", new
        {
            legalName = "Copycat Hotels",
            ownerEmail = email.ToUpperInvariant(),
            ownerName = "Someone Else",
            country = "IN",
        });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Register_ConcurrentRequestsForTheSameEmail_ProduceExactlyOneBusiness()
    {
        // The real uniqueness test. The SELECT in the handler is a courtesy
        // that BOTH of these requests pass; only the unique index is atomic,
        // and the handler has to turn its violation into the same 409.
        using HttpClient client = factory.NewClient();
        string email = TenantApiFactory.UniqueEmail();

        object payload = new
        {
            legalName = "Race Hotels",
            ownerEmail = email,
            ownerName = "Racer",
            country = "IN",
        };

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/v1/businesses", payload)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);

        using IServiceScope scope = factory.Services.CreateScope();
        TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        string normalised = email.ToLowerInvariant();
        (await db.Businesses.CountAsync(b => b.OwnerEmail == normalised)).ShouldBe(1);

        foreach (HttpResponseMessage response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Register_WithAnInvalidEmail_Returns400WithPerFieldDetail()
    {
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/businesses", new
        {
            legalName = "",
            ownerEmail = "not-an-email",
            ownerName = "",
            country = "INDIA",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(json);

        problem.RootElement.GetProperty("code").GetString().ShouldBe("validation_failed");

        // camelCase, matching the JSON the client sent, so the frontend can
        // attach each message to the input that caused it.
        JsonElement errors = problem.RootElement.GetProperty("errors");
        errors.TryGetProperty("ownerEmail", out _).ShouldBeTrue();
        errors.TryGetProperty("legalName", out _).ShouldBeTrue();
        errors.TryGetProperty("country", out _).ShouldBeTrue();
    }

    internal static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out JsonElement code)
            ? code.GetString()
            : null;
    }
}
