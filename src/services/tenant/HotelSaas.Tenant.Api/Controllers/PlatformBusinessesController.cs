using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Application.PlatformAdmin;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.Tenant.Api.Controllers;

// Our side of the SaaS.
//
// A separate route prefix from day one, even though authorization only
// arrives at stage 5. That way stage 5 adds one attribute to this class
// rather than re-routing anything, and the cross-tenant surface is
// greppable in the meantime (ADR-0004).
//
// STAGE 5: [Authorize(Policy = "platform:businesses.manage")]
[ApiController]
[Route("api/v1/platform/businesses")]
[Produces("application/json")]
public sealed class PlatformBusinessesController(IBusinessQueries queries) : ControllerBase
{
    private const int DefaultPageSize = 25;

    [HttpGet]
    [EndpointSummary("Search all businesses")]
    [ProducesResponseType<PagedResult<BusinessSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<BusinessSummary>>> Search(
        [FromServices] SearchBusinessesHandler handler,
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] string? cursor,
        // Nullable: a non-nullable int bound from the query string would be
        // required, and omitting ?limit= would fail before the action ran.
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        BusinessStatus? parsedStatus = null;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, ignoreCase: true, out BusinessStatus value))
            {
                // Names the valid options rather than just refusing - the
                // caller cannot guess an enum they have never seen.
                return Error.Validation(
                    "tenant.unknown_status",
                    $"Unknown status. Expected one of: {string.Join(", ", Enum.GetNames<BusinessStatus>())}.")
                    .ToProblemActionResult();
            }

            parsedStatus = value;
        }

        PagedResult<BusinessSummary> page = await handler.HandleAsync(
            new SearchBusinessesQuery(q, parsedStatus, cursor, limit ?? DefaultPageSize),
            cancellationToken);

        return Ok(page);
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("One business, in full")]
    [ProducesResponseType<BusinessDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BusinessDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        BusinessDetail? detail = await queries.GetDetailAsync(id, cancellationToken);

        // 404, never 403: a 403 would confirm the business exists.
        return detail is null
            ? BusinessErrors.NotFound.ToProblemActionResult()
            : Ok(detail);
    }

    [HttpPost("{id:guid}/suspend")]
    [EndpointSummary("Suspend a business")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Suspend(
        Guid id,
        [FromBody] SuspendBusinessRequest request,
        [FromServices] SuspendBusinessValidator validator,
        [FromServices] SuspendBusinessHandler handler,
        CancellationToken cancellationToken)
    {
        // Assembled from the route AND the body, so the global filter never
        // saw it as a single argument. Validated explicitly.
        SuspendBusinessCommand command = new(id, request.Reason);

        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        return (await handler.HandleAsync(command, cancellationToken)).ToActionResult();
    }

    [HttpPost("{id:guid}/activate")]
    [EndpointSummary("Un-suspend a business")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Activate(
        Guid id,
        [FromServices] ActivateBusinessHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(new ActivateBusinessCommand(id), cancellationToken)).ToActionResult();

    [HttpPost("{id:guid}/archive")]
    [EndpointSummary("Archive a business (terminal)")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Archive(
        Guid id,
        [FromServices] ArchiveBusinessHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(new ArchiveBusinessCommand(id), cancellationToken)).ToActionResult();
}

public sealed record SuspendBusinessRequest(string Reason);
