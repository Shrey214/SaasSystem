using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace HotelSaas.Tenant.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Produces("application/json")]
public sealed class OperationsController(TenantDbContext db, IHostEnvironment environment) : ControllerBase
{
    // Liveness. Touches nothing.
    //
    // Must answer even when postgres is down, because "the database is
    // unreachable" is not a reason to restart the process - restarting it
    // fixes nothing and loses the buffered error log.
    [HttpGet("/health")]
    [EndpointSummary("Liveness - is the process running")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Health() => Ok(new { status = "healthy", service = "tenant" });

    // Readiness. Should this instance receive traffic?
    //
    // Separate from liveness on purpose: this one SHOULD fail when the
    // database is unreachable, so the load balancer stops sending requests
    // while the process stays alive.
    [HttpGet("/health/ready")]
    [EndpointSummary("Readiness - can this instance serve traffic")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Ready(CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { status = "not_ready", reason = "database_unreachable" });
        }

        IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);

        // Pending migrations mean the code and the schema disagree, which is
        // worse than being down: it half-works.
        if (pending.Any())
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { status = "not_ready", reason = "pending_migrations" });
        }

        return Ok(new { status = "ready" });
    }

    // Proves the exception middleware records an error_logs row with the
    // correct file and line.
    //
    // Returns 404 outside Development rather than being conditionally
    // registered: a controller action cannot be added to the route table
    // only in some environments, so the guard lives in the action. The
    // effect is the same - there is no deployed endpoint that throws.
    [HttpGet("/boom")]
    [EndpointSummary("Development only - throws, to verify error logging")]
    public ActionResult Boom()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        throw new InvalidOperationException(
            "Deliberate failure from /boom, to verify error_logs capture.");
    }
}
