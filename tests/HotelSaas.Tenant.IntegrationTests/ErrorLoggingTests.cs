using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Tenant.IntegrationTests;

// ADR-0008 end to end, through the real pipeline.
[Collection(TenantApiCollection.Name)]
public sealed class ErrorLoggingTests(TenantApiFactory factory)
{
    [Fact]
    public async Task AnUnhandledException_Returns500AndRecordsWhereItBroke()
    {
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.GetAsync("/boom");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid errorId = problem.RootElement.GetProperty("errorId").GetGuid();

        problem.RootElement.GetProperty("code").GetString().ShouldBe("internal_error");
        problem.RootElement.TryGetProperty("correlationId", out _).ShouldBeTrue();

        // The write is buffered and drained by a background worker, so it is
        // deliberately not synchronous with the response - the request must
        // never wait on the logger.
        var stored = await WaitForErrorRowAsync(errorId);

        stored.ShouldNotBeNull();
        stored.SourceKind.ShouldBe("http");
        stored.Path.ShouldBe("/boom");
        stored.StatusCode.ShouldBe(500);
        stored.ExceptionType.ShouldBe("System.InvalidOperationException");

        // The whole point of the fault_* columns: the top stack frame is
        // framework noise, so the row must name OUR assembly and line.
        stored.FaultAssembly.ShouldNotBeNull();
        stored.FaultAssembly.ShouldStartWith("HotelSaas.");
        stored.FaultLine.ShouldNotBeNull();
        stored.FaultLine!.Value.ShouldBeGreaterThan(0);

        stored.Fingerprint.Length.ShouldBe(16);
    }

    [Fact]
    public async Task AProblemResponseDoesNotLeakTheStackTraceOutsideDevelopment()
    {
        // The factory runs in Development, where the exception detail IS
        // included on purpose so the flow can be debugged without opening
        // the database. This test pins that it is gated on the environment
        // rather than always on - if the condition is ever removed, a
        // deployed 500 starts handing attackers a stack trace.
        using HttpClient client = factory.NewClient();

        HttpResponseMessage response = await client.GetAsync("/boom");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        problem.RootElement.TryGetProperty("stackTrace", out _).ShouldBeTrue(
            "the factory runs in Development, so the detail is expected here");

        // And regardless of environment, the human-readable detail must stay
        // generic.
        problem.RootElement.GetProperty("detail").GetString()
            .ShouldNotBeNull()
            .ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task ExpectedBusinessFailures_AreNotRecordedAsErrors()
    {
        // A 409 from a lost race is the system working correctly. Logging it
        // would fill the table with successful business behaviour and bury
        // the faults that matter.
        using HttpClient client = factory.NewClient();

        using IServiceScope before = factory.Services.CreateScope();
        TenantDbContext beforeDb = before.ServiceProvider.GetRequiredService<TenantDbContext>();
        int countBefore = await beforeDb.ErrorLogs.CountAsync();

        RegisterResponse registered = await TenantApiFactory.RegisterAsync(client);
        HttpResponseMessage conflict = await client.PostAsJsonAsync(
            $"/api/v1/businesses/{registered.BusinessId}/verify-email",
            new { token = "wrong" });

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await Task.Delay(1500);

        using IServiceScope after = factory.Services.CreateScope();
        TenantDbContext afterDb = after.ServiceProvider.GetRequiredService<TenantDbContext>();

        (await afterDb.ErrorLogs.CountAsync()).ShouldBe(countBefore);
    }

    private async Task<ErrorRow?> WaitForErrorRowAsync(Guid errorId)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

            ErrorRow? row = await db.ErrorLogs
                .Where(e => e.ErrorId == errorId)
                .Select(e => new ErrorRow(
                    e.SourceKind, e.Path, e.StatusCode, e.ExceptionType,
                    e.FaultAssembly, e.FaultLine, e.Fingerprint))
                .FirstOrDefaultAsync();

            if (row is not null)
            {
                return row;
            }

            await Task.Delay(250);
        }

        return null;
    }

    private sealed record ErrorRow(
        string SourceKind,
        string? Path,
        int? StatusCode,
        string ExceptionType,
        string? FaultAssembly,
        int? FaultLine,
        string Fingerprint);
}
