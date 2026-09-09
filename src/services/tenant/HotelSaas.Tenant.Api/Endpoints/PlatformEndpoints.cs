using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Application.PlatformAdmin;
using HotelSaas.Tenant.Domain.Businesses;

namespace HotelSaas.Tenant.Api.Endpoints;

// Our side of the SaaS.
//
// A separate route prefix from day one, even though authorization only
// arrives at stage 5. That way stage 5 adds an attribute to this group
// rather than re-routing anything, and the cross-tenant surface is
// greppable in the meantime (ADR-0004).
internal static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder platform = app
            .MapGroup("/api/v1/platform/businesses")
            .WithTags("Platform administration");

        // STAGE 5: .RequireAuthorization("platform:businesses.manage")

        platform.MapGet("/", SearchAsync).WithSummary("Search all businesses");
        platform.MapGet("/{id:guid}", GetAsync).WithSummary("One business, in full");
        platform.MapPost("/{id:guid}/suspend", SuspendAsync).WithSummary("Suspend a business");
        platform.MapPost("/{id:guid}/activate", ActivateAsync).WithSummary("Un-suspend a business");
        platform.MapPost("/{id:guid}/archive", ArchiveAsync).WithSummary("Archive a business (terminal)");
    }

    private static async Task<IResult> SearchAsync(
        SearchBusinessesHandler handler,
        string? q,
        string? status,
        string? cursor,
        // Nullable for the same reason as the status-history endpoint: a
        // non-nullable int bound from the query string is required, and
        // omitting ?limit= would fail the request before the handler runs.
        int? limit,
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
                    .ToProblemResult();
            }

            parsedStatus = value;
        }

        PagedResult<BusinessSummary> page = await handler.HandleAsync(
            new SearchBusinessesQuery(q, parsedStatus, cursor, limit ?? 25),
            cancellationToken);

        return Results.Ok(page);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IBusinessQueries queries,
        CancellationToken cancellationToken)
    {
        BusinessDetail? detail = await queries.GetDetailAsync(id, cancellationToken);

        return detail is null
            ? BusinessErrors.NotFound.ToProblemResult()
            : Results.Ok(detail);
    }

    private static async Task<IResult> SuspendAsync(
        Guid id,
        SuspendBusinessRequest request,
        SuspendBusinessValidator validator,
        SuspendBusinessHandler handler,
        CancellationToken cancellationToken)
    {
        SuspendBusinessCommand command = new(id, request.Reason);

        // The shared helper, not Results.ValidationProblem: that produces a
        // different body (no `code`, PascalCase field names, a different
        // `type` URI), and docs/00-conventions.md 5 says no service invents
        // its own error shape.
        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        return (await handler.HandleAsync(command, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> ActivateAsync(
        Guid id,
        ActivateBusinessHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(new ActivateBusinessCommand(id), cancellationToken)).ToHttpResult();

    private static async Task<IResult> ArchiveAsync(
        Guid id,
        ArchiveBusinessHandler handler,
        CancellationToken cancellationToken)
        => (await handler.HandleAsync(new ArchiveBusinessCommand(id), cancellationToken)).ToHttpResult();
}

internal sealed record SuspendBusinessRequest(string Reason);
