using HotelSaas.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace HotelSaas.Identity.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Produces("application/json")]
public sealed class OperationsController(
    IdentityServiceDbContext db,
    IHostEnvironment environment) : ControllerBase
{
    [HttpGet("/health")]
    [EndpointSummary("Liveness - is the process running")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Health() => Ok(new { status = "healthy", service = "identity" });

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

        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { status = "not_ready", reason = "pending_migrations" });
        }

        // Part 2 adds a third check here: an active signing key. Without
        // one this service cannot issue a token, so it is ready in the
        // "database is up" sense and useless in every other sense.
        return Ok(new { status = "ready" });
    }

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
