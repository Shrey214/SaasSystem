using HotelSaas.BuildingBlocks.Observability;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Identity.Infrastructure;
using HotelSaas.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.UseHotelSaasLogging();

builder.Services.AddIdentityInfrastructure(builder.Configuration);

// No tenant header stub here, ever.
//
// This service ISSUES the tokens that carry tenant_id; reading a tenant
// from a forgeable header would be circular. Its own endpoints are either
// anonymous (login) or authenticated by a token it just signed.
builder.Services.AddHotelSaasWeb();

builder.Services.AddHotelSaasControllers();

builder.Services.AddHotelSaasOpenApi(
    serviceName: "identity",
    description:
        "Authentication and authorization. Issues RS256 access tokens and " +
        "rotating refresh tokens; owns users, tenant memberships and " +
        "invitations. No external identity provider - see ADR-0005.",
    includeTenantHeaderScheme: false);

WebApplication app = builder.Build();

app.UseHotelSaasPipeline(useAuthentication: false);
app.UseHotelSaasRequestLogging();

app.MapControllers();
app.UseHotelSaasSwagger("identity");

if (app.Environment.IsDevelopment())
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    IdentityServiceDbContext db = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();
