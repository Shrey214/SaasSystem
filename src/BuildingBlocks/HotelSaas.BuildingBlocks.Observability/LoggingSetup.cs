using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace HotelSaas.BuildingBlocks.Observability;

// Structured logging to stdout, identical in all 14 services.
//
// stdout is the AUTHORITATIVE record (ADR-0008). It works when the database
// is the thing that is broken, which is exactly when the error_logs table
// cannot be written. The table is the queryable convenience that powers the
// platform-admin support screens; this is the source of truth.
public static class LoggingSetup
{
    // Called before anything else in Program.cs, so a crash during startup
    // is still logged rather than vanishing.
    public static void UseHotelSaasLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", context.HostingEnvironment.ApplicationName)
            .Enrich.WithProperty("environment", context.HostingEnvironment.EnvironmentName)
            // Serilog.Enrichers.Environment exists for this, but one
            // property is not worth another package.
            .Enrich.WithProperty("machine", Environment.MachineName)

            // EF logs every command at Information, which drowns everything
            // else. Warnings from EF still come through.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)

            .WriteTo.Console(
                // Development: readable in a terminal. Anywhere else: JSON,
                // because a log collector parses it and a human does not.
                formatter: context.HostingEnvironment.IsDevelopment()
                    ? new Serilog.Formatting.Display.MessageTemplateTextFormatter(
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {NewLine}{Exception}")
                    : new Serilog.Formatting.Compact.CompactJsonFormatter()));
    }

    // Request logging, with the correlation id attached.
    //
    // Registered after the exception middleware so a failed request is still
    // logged once, with its status code, rather than only as an exception.
    public static void UseHotelSaasRequestLogging(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging(options =>
        {
            // One line per request instead of the framework default of four.
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0}ms";

            options.EnrichDiagnosticContext = (diagnostic, httpContext) =>
            {
                diagnostic.Set("correlationId", httpContext.Items["hs.correlation_id"]);
                diagnostic.Set("tenantId", httpContext.Items["hs.tenant_id"]);
            };

            // Health checks are noise at Information; keep them at Verbose
            // so a failing one is still visible.
            options.GetLevel = (httpContext, elapsed, exception) =>
                exception is not null || httpContext.Response.StatusCode >= 500
                    ? LogEventLevel.Error
                    : httpContext.Request.Path.StartsWithSegments("/health")
                        ? LogEventLevel.Verbose
                        : LogEventLevel.Information;
        });
    }
}
