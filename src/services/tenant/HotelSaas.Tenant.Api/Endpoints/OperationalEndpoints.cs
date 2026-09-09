using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Tenant.Api.Endpoints;

internal static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness. Touches nothing.
        //
        // Must answer even when postgres is down, because "the database is
        // unreachable" is not a reason to restart the process - restarting it
        // fixes nothing and loses the buffered error log.
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            service = "tenant",
        }))
        .WithTags("Operations")
        .WithSummary("Liveness - is the process running");

        // Readiness. Should this instance receive traffic?
        //
        // Separate from liveness on purpose: this one SHOULD fail when the
        // database is unreachable, so the load balancer stops sending
        // requests while the process stays alive.
        app.MapGet("/health/ready", async (TenantDbContext db, CancellationToken ct) =>
        {
            bool canConnect = await db.Database.CanConnectAsync(ct);

            if (!canConnect)
            {
                return Results.Json(
                    new { status = "not_ready", reason = "database_unreachable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync(ct);

            // Pending migrations mean the code and the schema disagree, which
            // is worse than being down: it half-works.
            if (pending.Any())
            {
                return Results.Json(
                    new { status = "not_ready", reason = "pending_migrations" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { status = "ready" });
        })
        .WithTags("Operations")
        .WithSummary("Readiness - can this instance serve traffic");

        // Development only.
        //
        // Proves the exception middleware records a row in error_logs with
        // the correct file and line. It is registered only in Development, so
        // there is no deployed endpoint that deliberately throws.
        app.MapGet("/boom", IResult () => throw new InvalidOperationException(
                "Deliberate failure from /boom, to verify error_logs capture."))
            .WithTags("Operations")
            .WithSummary("Development only - throws, to verify error logging");
    }
}
