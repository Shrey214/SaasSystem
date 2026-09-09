using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Persistence;
using HotelSaas.Tenant.Application.Abstractions;
using HotelSaas.Tenant.Application.Businesses.RegisterBusiness;
using HotelSaas.Tenant.Application.Businesses.ResendVerification;
using HotelSaas.Tenant.Application.Businesses.UpdateProfile;
using HotelSaas.Tenant.Application.Businesses.VerifyEmail;
using HotelSaas.Tenant.Application.PlatformAdmin;
using HotelSaas.Tenant.Infrastructure.Persistence;
using HotelSaas.Tenant.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Tenant.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddTenantInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString("TenantDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:TenantDb is not configured. The service connects as " +
                "hs_tenant_user, never as postgres - see ADR-0003.");

        services.AddDbContext<TenantDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history"))

            // Table and column names come from here rather than from 200
            // lines of HasColumnName (docs/00-conventions.md 3).
            .UseSnakeCaseNamingConvention());

        // Both resolve to the SAME scoped DbContext, so a handler that loads
        // an aggregate and then commits is working in one transaction.
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TenantDbContext>());
        services.AddScoped<IBusinessRepository, BusinessRepository>();
        services.AddScoped<IBusinessQueries, BusinessQueries>();

        services.AddSingleton<IVerificationTokenGenerator, VerificationTokenGenerator>();
        services.AddScoped<IVerificationDispatcher, LoggingVerificationDispatcher>();

        services.AddHotelSaasPersistence(options =>
        {
            options.ConnectionString = connectionString;
            options.ServiceName = "tenant";
            options.Environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        });

        AddUseCases(services);

        return services;
    }

    // Registered explicitly rather than by assembly scanning.
    //
    // MediatR moved to a commercial licence, and reflection-based
    // registration hides a missing dependency until the endpoint is called.
    // The cost is one line per use case; the benefit is that the container
    // fails at startup instead of on a customer request.
    private static void AddUseCases(IServiceCollection services)
    {
        services.AddScoped<RegisterBusinessHandler>();
        services.AddScoped<VerifyEmailHandler>();
        services.AddScoped<ResendVerificationHandler>();
        services.AddScoped<UpdateProfileHandler>();
        services.AddScoped<SuspendBusinessHandler>();
        services.AddScoped<ActivateBusinessHandler>();
        services.AddScoped<ArchiveBusinessHandler>();
        services.AddScoped<SearchBusinessesHandler>();

        services.AddScoped<RegisterBusinessValidator>();
        services.AddScoped<VerifyEmailValidator>();
        services.AddScoped<ResendVerificationValidator>();
        services.AddScoped<UpdateProfileValidator>();
        services.AddScoped<SuspendBusinessValidator>();
    }
}
