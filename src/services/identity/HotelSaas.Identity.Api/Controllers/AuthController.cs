using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Identity.Application.Access.AcceptInvitation;
using HotelSaas.Identity.Application.Access.Login;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.Identity.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    // Finish setup: turn an invitation into a usable account.
    //
    // This is the only endpoint in the whole system that accepts a
    // password. The tenant service never sees one.
    [HttpPost("accept-invitation")]
    [EndpointSummary("Set a password using an invitation link, completing signup")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> AcceptInvitation(
        [FromBody] AcceptInvitationCommand command,
        [FromServices] AcceptInvitationHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(command, cancellationToken)).ToActionResult();

    [HttpPost("login")]
    [EndpointSummary("Exchange email and password for an access token")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginCommand command,
        [FromServices] LoginHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(command, cancellationToken)).ToActionResult();
}
