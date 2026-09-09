using System.Security.Claims;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Web.Middleware;
using Microsoft.AspNetCore.Http;

namespace HotelSaas.BuildingBlocks.Web;

// Reads the tenant resolved by TenantContextMiddleware.
//
// Scoped, and it exposes no setter: nothing downstream can change which
// tenant the request belongs to (ADR-0004 layer 1).
internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid? TenantId => accessor.HttpContext is { } context
        ? TenantContextMiddleware.GetTenantId(context)
        : null;
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            string? raw = accessor.HttpContext?.User?.FindFirstValue("sub");
            return Guid.TryParse(raw, out Guid id) ? id : null;
        }
    }

    // Populated from the props claim once stage 5 issues tokens. Empty until
    // then, which fails closed: no claim means no property access.
    public IReadOnlySet<Guid> AuthorizedPropertyIds
    {
        get
        {
            ClaimsPrincipal? user = accessor.HttpContext?.User;
            if (user is null)
            {
                return new HashSet<Guid>();
            }

            HashSet<Guid> ids = [];
            foreach (Claim claim in user.FindAll("props"))
            {
                if (Guid.TryParse(claim.Value, out Guid id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }
}

// Reads the correlation id set by CorrelationIdMiddleware.
//
// Falls back to a fresh id rather than throwing: a background job or a test
// has no HttpContext, and an event with a new correlation id is far better
// than a failed save.
internal sealed class HttpCorrelationContext(IHttpContextAccessor accessor) : ICorrelationContext
{
    private Guid? _fallback;

    public Guid CorrelationId
    {
        get
        {
            if (accessor.HttpContext is { } context
                && CorrelationIdMiddleware.GetCorrelationId(context) is { } id)
            {
                return id;
            }

            return _fallback ??= Domain.Uuid7.New();
        }
    }
}
