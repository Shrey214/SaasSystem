using FluentValidation;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Persistence;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Application.Access.AcceptInvitation;
using HotelSaas.Identity.Application.Access.CreateOwnerInvitation;
using HotelSaas.Identity.Application.Access.Login;
using HotelSaas.Identity.Infrastructure.Persistence;
using HotelSaas.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HotelSaas.Identity.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString("IdentityDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:IdentityDb is not configured. The service connects as " +
                "hs_identity_user, never as postgres - see ADR-0003.");

        services.AddDbContext<IdentityServiceDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<IdentityServiceDbContext>());

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<IInvitationRepository, InvitationRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<ISigningKeyStore, SigningKeyStore>();
        services.AddScoped<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        services.AddScoped<CreateOwnerInvitationHandler>();
        services.AddScoped<AcceptInvitationHandler>();
        services.AddScoped<LoginHandler>();

        // Validators by assembly scan, handlers explicitly - a validator is
        // a leaf object with nothing for reflection to hide, a handler has
        // injected dependencies that should fail at startup if missing.
        services.AddValidatorsFromAssemblyContaining<LoginValidator>(ServiceLifetime.Scoped);
        services.AddScoped<AcceptInvitationValidator>();
        services.AddScoped<CreateOwnerInvitationValidator>();
        services.AddScoped<LoginValidator>();

        AddIdentityCore(services);

        services.AddHotelSaasPersistence(options =>
        {
            options.ConnectionString = connectionString;
            options.ServiceName = "identity";
            options.Environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
        });

        return services;
    }

    // AddIdentityCore, not AddIdentity.
    //
    // AddIdentity wires up cookie authentication, sign-in managers and the
    // whole MVC-era login pipeline. This service issues JWTs and has no
    // cookies, no login pages and no external providers - the only parts
    // wanted are the user store, the password hasher and the validators.
    private static void AddIdentityCore(IServiceCollection services)
    {
        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                // Length over character-class rules. Requiring a digit and
                // a symbol reliably produces Password1! and nothing safer;
                // 12 characters is a real constraint. NIST 800-63B has said
                // so for years and most products still ignore it.
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;

                options.User.RequireUniqueEmail = true;

                // Lockout is the only thing standing between a login form
                // and unlimited password guessing, and stage 5 has no rate
                // limiting yet - Kong arrives at 5c.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                // Email confirmation is handled by the invitation flow: a
                // user cannot get a password without following a link sent
                // to that address, so the address is proven by construction.
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddEntityFrameworkStores<IdentityServiceDbContext>();

        // No AddDefaultTokenProviders(). Those providers exist to mint
        // password-reset and email-confirmation tokens, and this service
        // mints its own (Invitation, with a 72h expiry and a stored hash)
        // because the invitation flow has to carry a tenant id that
        // Identity's providers know nothing about.
    }
}
