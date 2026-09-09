using System.Text.Json;
using System.Text.Json.Serialization;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HotelSaas.BuildingBlocks.Web;

public static class WebServiceCollectionExtensions
{
    public static IServiceCollection AddHotelSaasWeb(
        this IServiceCollection services,
        Action<TenantContextOptions>? configureTenant = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        TenantContextOptions options = new();
        configureTenant?.Invoke(options);
        services.TryAddSingleton(options);

        services.AddHttpContextAccessor();
        services.TryAddScoped<ITenantContext, HttpTenantContext>();
        services.TryAddScoped<ICurrentUser, HttpCurrentUser>();
        services.TryAddScoped<ICorrelationContext, HttpCorrelationContext>();

        return services;
    }

    // Controllers, configured identically in all 14 services.
    public static IMvcBuilder AddHotelSaasControllers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        IMvcBuilder builder = services
            .AddControllers(options => options.Filters.Add<FluentValidationFilter>())
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

                // Accept "Active" as well as 0 for an enum, and always write
                // the name. A status that serialises as an integer is
                // unreadable in a log and breaks the moment the enum is
                // reordered.
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.Configure<ApiBehaviorOptions>(options =>
        {
            // REPLACED, not suppressed.
            //
            // [ApiController] would otherwise emit its own
            // ValidationProblemDetails - a second error shape inside the
            // same service, with no `code` field and a different `type` URI
            // (docs/00-conventions.md 5).
            //
            // Suppressing it outright was the first attempt and it opened a
            // hole: model binding still FAILS, it just stops short-circuiting,
            // so the action runs with a null argument and dies of an
            // ArgumentNullException - a 500 for the caller's typo. Replacing
            // the factory keeps the short-circuit and fixes the shape.
            options.InvalidModelStateResponseFactory = BuildProblemFromModelState;

            // No automatic ProblemDetails for bare status results either.
            // Everything goes through ProblemDetailsFactory.
            options.SuppressMapClientErrors = true;
        });

        return builder;
    }

    private static ObjectResult BuildProblemFromModelState(ActionContext context)
    {
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        // A parse failure is reported against the JSON ROOT - an empty key or
        // "$" - because there was no field to attach it to. That is the
        // reliable signal, not error.Exception: the JSON input formatter
        // converts a JsonException into a model MESSAGE and clears the
        // exception, so checking for one never fires.
        bool isBindingFailure = context.ModelState
            .Any(entry => entry.Value?.Errors.Count > 0 && entry.Key is "" or "$");

        foreach ((string key, ModelStateEntry entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0)
            {
                continue;
            }

            bool isRootError = key is "" or "$";

            // When the body could not be parsed, the parameter itself is ALSO
            // reported as missing ("The command field is required"), which is
            // true but noise - the caller has one problem, not two.
            if (isBindingFailure && !isRootError)
            {
                continue;
            }

            string[] messages = entry.Errors
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "The value could not be read."
                    : error.ErrorMessage)
                .ToArray();

            errors[isRootError ? "body" : ToCamelCase(key)] = messages;
        }

        ProblemDetails problem = isBindingFailure
            ? ProblemDetailsFactory.Create(
                Domain.Error.Validation(
                    "malformed_request",
                    "The request could not be read. Check the body and the query string."),
                context.HttpContext)
            : ProblemDetailsFactory.CreateValidation(errors, context.HttpContext);

        if (isBindingFailure && errors.Count > 0)
        {
            problem.Extensions["errors"] = errors;
        }

        return new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
    }

    private static string ToCamelCase(string name)
        => string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}

public static class HotelSaasApplicationBuilderExtensions
{
    // The pipeline, in the only order that works.
    //
    // Order here is behaviour, not preference:
    //  1. correlation first, so an exception below already has an id
    //  2. exception handling second, so it also catches auth failures
    //  3. authentication before tenant, because the tenant comes from
    //     validated claims and never from a header (ADR-0004)
    public static IApplicationBuilder UseHotelSaasPipeline(
        this IApplicationBuilder app,
        bool useAuthentication = true)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        if (useAuthentication)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseMiddleware<TenantContextMiddleware>();

        return app;
    }
}
