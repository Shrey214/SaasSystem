using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Application.Businesses.RegisterBusiness;
using HotelSaas.Tenant.Application.Businesses.ResendVerification;
using HotelSaas.Tenant.Application.Businesses.UpdateProfile;
using HotelSaas.Tenant.Application.Businesses.VerifyEmail;

namespace HotelSaas.Tenant.Api.Endpoints;

internal static class BusinessEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static void MapBusinessEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder pub = app.MapGroup("/api/v1/businesses").WithTags("Businesses");

        // ---- public: the only endpoints a stranger can reach ----

        pub.MapPost("/", RegisterAsync)
            .WithSummary("Register a business")
            .AllowAnonymous();

        pub.MapPost("/{id:guid}/verify-email", VerifyEmailAsync)
            .WithSummary("Confirm the owner email address, which activates the business")
            .AllowAnonymous();

        pub.MapPost("/resend-verification", ResendVerificationAsync)
            .WithSummary("Send a fresh verification link")
            .AllowAnonymous();

        // ---- the caller's own business ----
        //
        // /me, not /{id}. The business id IS the tenant id and already comes
        // from the token, so accepting it in the URL as well would create
        // exactly the tamperable parameter ADR-0004 forbids.

        pub.MapGet("/me", GetMineAsync).WithSummary("The caller's own business");
        pub.MapGet("/me/profile", GetMyProfileAsync).WithSummary("Address, contact and branding");
        pub.MapPut("/me/profile", UpdateMyProfileAsync).WithSummary("Update the profile");
        pub.MapGet("/me/status-history", GetMyStatusHistoryAsync).WithSummary("Lifecycle audit trail");
    }

    private static async Task<IResult> RegisterAsync(
        RegisterBusinessCommand command,
        RegisterBusinessValidator validator,
        RegisterBusinessHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        Result<RegisterBusinessResponse> result = await handler.HandleAsync(command, cancellationToken);

        // 201 with a Location header, so the client learns where the new
        // resource lives instead of having to build the URL itself.
        return result.ToCreatedResult(response => $"/api/v1/businesses/{response.BusinessId}");
    }

    private static async Task<IResult> VerifyEmailAsync(
        Guid id,
        VerifyEmailRequest request,
        VerifyEmailValidator validator,
        VerifyEmailHandler handler,
        CancellationToken cancellationToken)
    {
        VerifyEmailCommand command = new(id, request.Token);

        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        return (await handler.HandleAsync(command, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> ResendVerificationAsync(
        ResendVerificationCommand command,
        ResendVerificationValidator validator,
        ResendVerificationHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        Result result = await handler.HandleAsync(command, cancellationToken);

        // 202 whatever happened. This endpoint is keyed by email with no
        // registration attempt behind it, so a distinguishable response
        // would turn it into a free address-checking oracle.
        return result.IsSuccess ? Results.Accepted() : result.ToHttpResult();
    }

    private static async Task<IResult> GetMineAsync(
        ITenantContext tenant,
        IBusinessQueries queries,
        CancellationToken cancellationToken)
    {
        BusinessDetail? detail = await queries.GetDetailAsync(tenant.RequireTenantId(), cancellationToken);

        return detail is null
            ? Domain.Businesses.BusinessErrors.NotFound.ToProblemResult()
            : Results.Ok(detail);
    }

    private static async Task<IResult> GetMyProfileAsync(
        ITenantContext tenant,
        IBusinessQueries queries,
        CancellationToken cancellationToken)
    {
        BusinessProfileDto? profile = await queries.GetProfileAsync(tenant.RequireTenantId(), cancellationToken);

        return profile is null
            ? Domain.Businesses.BusinessErrors.NotFound.ToProblemResult()
            : Results.Ok(profile);
    }

    private static async Task<IResult> UpdateMyProfileAsync(
        UpdateProfileCommand command,
        UpdateProfileValidator validator,
        UpdateProfileHandler handler,
        CancellationToken cancellationToken)
    {
        if (RequestValidation.Validate(validator, command) is { } problem)
        {
            return problem;
        }

        return (await handler.HandleAsync(command, cancellationToken)).ToHttpResult();
    }

    private static async Task<IResult> GetMyStatusHistoryAsync(
        ITenantContext tenant,
        IBusinessQueries queries,
        string? cursor,
        // Nullable, with a default. A non-nullable value type bound from the
        // query string is REQUIRED in minimal APIs, so plain `int limit`
        // makes ?limit= mandatory and omitting it throws
        // BadHttpRequestException before the handler is even reached.
        int? limit,
        CancellationToken cancellationToken)
    {
        PagedResult<StatusChangeDto> page = await queries.GetStatusHistoryAsync(
            tenant.RequireTenantId(),
            cursor,
            Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize),
            cancellationToken);

        return Results.Ok(page);
    }

}

// The token arrives in the body, never the query string: query strings end
// up in access logs, browser history and Referer headers.
internal sealed record VerifyEmailRequest(string Token);
