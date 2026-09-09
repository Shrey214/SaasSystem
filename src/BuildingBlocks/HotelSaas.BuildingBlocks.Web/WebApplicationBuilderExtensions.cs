using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
