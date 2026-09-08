using HotelSaas.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Http;

namespace HotelSaas.BuildingBlocks.Web.Middleware;

// Gives every request a correlation id and propagates it
// (docs/03-communication.md 5).
//
// Registered FIRST, so anything that fails below already has an id to log
// against. One value spans a whole customer booking across nine services,
// which is what makes "why did this booking not confirm" answerable.
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    private const string ItemKey = "hs.correlation_id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Guid correlationId = TryReadFromHeader(context, out Guid incoming)
            ? incoming
            : Uuid7.New();

        context.Items[ItemKey] = correlationId;

        // Echoed back so a caller can quote it, and set before the response
        // starts because headers cannot be added afterwards.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId.ToString();
            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }

    public static Guid? GetCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out object? value) && value is Guid id ? id : null;
    }

    private static bool TryReadFromHeader(HttpContext context, out Guid correlationId)
    {
        correlationId = Guid.Empty;
        string? raw = context.Request.Headers[HeaderName].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out correlationId);
    }
}
