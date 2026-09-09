using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace HotelSaas.BuildingBlocks.Web;

// Swagger for every service, configured once.
//
// The DOCUMENT comes from Microsoft.AspNetCore.OpenApi, which is built into
// the framework and reads the [HttpPost], [ProducesResponseType] and
// [EndpointSummary] attributes the controllers already carry. Only the UI
// comes from Swashbuckle, which is the supported split in .NET 9+ - full
// Swashbuckle would generate a second, competing document.
public static class OpenApiSetup
{
    private const string TenantHeaderScheme = "TenantIdHeader";

    public static IServiceCollection AddHotelSaasOpenApi(
        this IServiceCollection services,
        string serviceName,
        string description,
        bool includeTenantHeaderScheme)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = $"HotelSaas — {serviceName}",
                    Version = "v1",
                    Description = description,
                };

                if (includeTenantHeaderScheme)
                {
                    AddTenantHeaderScheme(document);
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    // Swagger UI, and the OpenAPI document behind it.
    //
    // DEVELOPMENT ONLY. An interactive, self-documenting client for every
    // endpoint - including the ones a platform admin uses - is not something
    // to expose on a deployed instance. Stage 5 puts Kong in front and the
    // decision about a published, curated document belongs there.
    public static WebApplication UseHotelSaasSwagger(this WebApplication app, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        // Serves /openapi/v1.json
        app.MapOpenApi();

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", $"{serviceName} v1");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = $"HotelSaas — {serviceName}";

            // Collapsed by default: with 15 endpoints the expanded view is a
            // wall of text, and it only gets worse in booking.
            options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
            options.DefaultModelsExpandDepth(0);
            options.EnableTryItOutByDefault();
            options.DisplayRequestDuration();
        });

        // Landing on the root of a service in a browser should go somewhere
        // useful rather than 404.
        app.MapGet("/", () => Results.Redirect("/swagger"))
            .ExcludeFromDescription();

        return app;
    }

    // ==================== TEMPORARY - STAGES 4 TO 5 ====================
    // Declares X-Tenant-Id as an apiKey scheme so Swagger UI shows an
    // Authorize button, and then sends the header on every request. Without
    // it the /me endpoints cannot be tried from the UI at all.
    //
    // This mirrors the forgeable header stub in TenantContextMiddleware and
    // disappears with it at stage 5, replaced by a bearer scheme pointing at
    // the identity service (ADR-0005).
    // ===================================================================
    private static void AddTenantHeaderScheme(OpenApiDocument document)
    {
        OpenApiSecurityScheme scheme = new()
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Tenant-Id",
            Description =
                "DEVELOPMENT ONLY. The business id to act as, for the /me endpoints. " +
                "Register a business first and paste its businessId here. " +
                "Stage 5 replaces this with a bearer token.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[TenantHeaderScheme] = scheme;

        // Applied document-wide rather than per operation. It is optional in
        // practice - the public endpoints ignore it - and marking each one
        // individually would be noise that all disappears at stage 5 anyway.
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(TenantHeaderScheme, document)] = [],
        });
    }
}
