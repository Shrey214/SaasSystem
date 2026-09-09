using HotelSaas.BuildingBlocks.Observability;
using HotelSaas.BuildingBlocks.Web;
using HotelSaas.Tenant.Infrastructure;
using HotelSaas.Tenant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// First, so a crash during startup is still logged rather than vanishing.
builder.UseHotelSaasLogging();

builder.Services.AddTenantInfrastructure(builder.Configuration);

builder.Services.AddHotelSaasWeb(tenant =>
{
    // ==================== TEMPORARY - STAGE 4 ONLY ====================
    // There is no authentication until stage 5, so the tenant comes from an
    // X-Tenant-Id header, which is trivially forged.
    //
    // Enabled ONLY in Development. A deployed instance gets no tenant at all
    // rather than a forgeable one, so this cannot quietly survive into an
    // environment that matters. Stage 5 deletes the option and the branch
    // behind it (ADR-0004).
    // ==================================================================
    tenant.AllowHeaderFallback = builder.Environment.IsDevelopment();
});

// Controllers, with the global FluentValidation filter and - importantly -
// [ApiController]'s own error shapes suppressed, so every problem response
// in all 14 services comes from ProblemDetailsFactory.
builder.Services.AddHotelSaasControllers();

builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Order is behaviour, not preference: correlation, then exception handling,
// then auth, then tenant. See UseHotelSaasPipeline.
app.UseHotelSaasPipeline(useAuthentication: false);
app.UseHotelSaasRequestLogging();

app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Migrations are applied at startup in Development only.
    //
    // Never in production: two instances starting together would both try to
    // migrate, and a schema change would be applied by whichever pod happened
    // to boot first. Stage 24 runs migrations as a separate deploy step.
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    TenantDbContext db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();
