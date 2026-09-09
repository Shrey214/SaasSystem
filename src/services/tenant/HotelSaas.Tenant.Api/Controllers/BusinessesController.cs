using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Application.Businesses.RegisterBusiness;
using HotelSaas.Tenant.Application.Businesses.ResendVerification;
using HotelSaas.Tenant.Application.Businesses.UpdateProfile;
using HotelSaas.Tenant.Application.Businesses.VerifyEmail;
using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.Tenant.Api.Controllers;

[ApiController]
[Route("api/v1/businesses")]
[Produces("application/json")]
public sealed class BusinessesController(ITenantContext tenantContext, IBusinessQueries queries)
    : ControllerBase
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    // Validation is not called here. FluentValidationFilter runs every
    // registered IValidator<T> against the action arguments before the
    // action body executes, so a controller only ever sees valid input.

    // ------------------------------------------------------------------
    // public: the only endpoints a stranger can reach
    // ------------------------------------------------------------------

    [HttpPost]
    [AllowAnonymous]
    [EndpointSummary("Register a business")]
    [ProducesResponseType<RegisterBusinessResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RegisterBusinessResponse>> Register(
        [FromBody] RegisterBusinessCommand command,
        [FromServices] RegisterBusinessHandler handler,
        CancellationToken cancellationToken)
    {
        Result<RegisterBusinessResponse> result = await handler.HandleAsync(command, cancellationToken);

        return result.ToCreatedResult(response => $"/api/v1/businesses/{response.BusinessId}");
    }

    [HttpPost("{id:guid}/verify-email")]
    [AllowAnonymous]
    [EndpointSummary("Confirm the owner email address, which activates the business")]
    [ProducesResponseType<VerifyEmailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<VerifyEmailResponse>> VerifyEmail(
        Guid id,
        [FromBody] VerifyEmailRequest request,
        [FromServices] VerifyEmailValidator validator,
        [FromServices] VerifyEmailHandler handler,
        CancellationToken cancellationToken)
    {
        // The command is assembled from the route AND the body, so the
        // filter cannot have validated it - it never appeared as an action
        // argument. Validated explicitly for that reason.
        VerifyEmailCommand command = new(id, request.Token);

        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        return (await handler.HandleAsync(command, cancellationToken)).ToActionResult();
    }

    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [EndpointSummary("Send a fresh verification link")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult> ResendVerification(
        [FromBody] ResendVerificationCommand command,
        [FromServices] ResendVerificationHandler handler,
        CancellationToken cancellationToken)
    {
        Result result = await handler.HandleAsync(command, cancellationToken);

        // 202 whatever happened. This endpoint is keyed by email with no
        // registration attempt behind it, so a distinguishable response
        // would turn it into a free address-checking oracle.
        return result.IsSuccess ? Accepted() : result.ToActionResult();
    }

    // ------------------------------------------------------------------
    // the caller's own business
    //
    // /me, not /{id}. The business id IS the tenant id and already comes
    // from the token, so accepting it in the URL as well would create
    // exactly the tamperable parameter ADR-0004 forbids.
    // ------------------------------------------------------------------

    [HttpGet("me")]
    [EndpointSummary("The caller's own business")]
    [ProducesResponseType<BusinessDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BusinessDetail>> GetMine(CancellationToken cancellationToken)
    {
        // Read path: projected straight to a DTO in SQL, never through the
        // aggregate (docs/05-code-structure.md 2.5).
        BusinessDetail? detail = await queries.GetDetailAsync(
            tenantContext.RequireTenantId(), cancellationToken);

        return detail is null
            ? BusinessErrors.NotFound.ToProblemActionResult()
            : Ok(detail);
    }

    [HttpGet("me/profile")]
    [EndpointSummary("Address, contact and branding")]
    [ProducesResponseType<BusinessProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BusinessProfileDto>> GetMyProfile(CancellationToken cancellationToken)
    {
        BusinessProfileDto? profile = await queries.GetProfileAsync(
            tenantContext.RequireTenantId(), cancellationToken);

        return profile is null
            ? BusinessErrors.NotFound.ToProblemActionResult()
            : Ok(profile);
    }

    [HttpPut("me/profile")]
    [EndpointSummary("Update the profile")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateMyProfile(
        [FromBody] UpdateProfileCommand command,
        [FromServices] UpdateProfileHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(command, cancellationToken)).ToActionResult();

    [HttpGet("me/status-history")]
    [EndpointSummary("Lifecycle audit trail")]
    [ProducesResponseType<PagedResult<StatusChangeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StatusChangeDto>>> GetMyStatusHistory(
        [FromQuery] string? cursor,
        // Nullable with a default. A non-nullable int bound from the query
        // string is REQUIRED, so plain `int limit` makes ?limit= mandatory.
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        PagedResult<StatusChangeDto> page = await queries.GetStatusHistoryAsync(
            tenantContext.RequireTenantId(),
            cursor,
            Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize),
            cancellationToken);

        return Ok(page);
    }
}

// The token arrives in the body, never the query string: query strings end
// up in access logs, browser history and Referer headers.
public sealed record VerifyEmailRequest(string Token);
