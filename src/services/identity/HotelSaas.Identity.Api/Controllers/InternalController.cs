using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Identity.Application.Access.CreateOwnerInvitation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.Identity.Api.Controllers;

// Service-to-service only. Never routed through the gateway.
//
// Called by tenant when a business finishes email verification, to create
// the owner's account and an invitation they can set a password with.
//
// ==================== NOT YET AUTHENTICATED ====================
// Stage 5c puts Kong in front and this route is excluded from the public
// listener there. That is routing, not authentication, and routing is not
// a security control - anything already inside the network can call this
// and create an owner invitation for any tenant id it likes.
//
// The real fix is service-to-service authentication (mTLS, or a signed
// service token), and it belongs with the gateway work rather than here.
// Written down rather than quietly shipped.
// ===============================================================
[ApiController]
[Route("internal")]
[AllowAnonymous]
[Produces("application/json")]
public sealed class InternalController : ControllerBase
{
    [HttpPost("owner-invitations")]
    [EndpointSummary("Create the owner account and invitation for a newly verified business")]
    [ProducesResponseType<CreateOwnerInvitationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateOwnerInvitationResponse>> CreateOwnerInvitation(
        [FromBody] CreateOwnerInvitationCommand command,
        [FromServices] CreateOwnerInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        Result<CreateOwnerInvitationResponse> result = await handler.HandleAsync(command, cancellationToken);

        return result.ToCreatedResult(r => $"/internal/owner-invitations/{r.InvitationId}");
    }
}
