using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace HotelSaas.BuildingBlocks.Web.Middleware;

// Resolves the tenant for the current request (ADR-0004 layer 1).
//
// ============================ TEMPORARY ============================
// Until stage 5 there is no authentication, so the tenant is read from an
// X-Tenant-Id HEADER. That is trivially forged and completely unacceptable
// beyond local development.
//
// Stage 5 replaces the header branch with the tenant_id claim from a
// validated RS256 token and DELETES AllowHeaderFallback. The switch is
// deliberately opt-in and defaults to false so it cannot survive quietly:
// a service that forgets to turn it on simply has no tenant.
// ===================================================================
public sealed class TenantContextMiddleware(RequestDelegate next, TenantContextOptions options)
{
    public const string HeaderName = "X-Tenant-Id";

    private const string ItemKey = "hs.tenant_id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Claims first, always. Once stage 5 lands, this is the only branch.
        Guid? tenantId = ReadFromClaims(context);

        if (tenantId is null && options.AllowHeaderFallback)
        {
            tenantId = ReadFromHeader(context);
        }

        context.Items[ItemKey] = tenantId;

        await next(context).ConfigureAwait(false);
    }

    public static Guid? GetTenantId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out object? value) && value is Guid id ? id : null;
    }

    private static Guid? ReadFromClaims(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? raw = context.User.FindFirstValue("tenant_id");
        return Guid.TryParse(raw, out Guid tenantId) ? tenantId : null;
    }

    private static Guid? ReadFromHeader(HttpContext context)
    {
        string? raw = context.Request.Headers[HeaderName].FirstOrDefault();
        return Guid.TryParse(raw, out Guid tenantId) ? tenantId : null;
    }
}

public sealed class TenantContextOptions
{
    // Stage 3 only. Stage 5 removes this and the code path behind it.
    public bool AllowHeaderFallback { get; set; }
}
