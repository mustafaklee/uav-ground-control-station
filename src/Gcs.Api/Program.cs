using Asp.Versioning;
using Gcs.Api;
using Gcs.Api.Endpoints;
using Gcs.Api.Http;
using Gcs.Api.Middleware;
using Gcs.Api.Observability;
using Gcs.Api.Realtime;
using Gcs.Api.Security;
using Gcs.Application;
using Gcs.Application.Abstractions;
using Gcs.Contracts.Realtime;
using Gcs.Infrastructure;
using Serilog;

// Minimal console logger so failures during startup (bad configuration, DI errors) are still visible.
// It is replaced by the configuration-driven logger as soon as the host is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteToOtlpIfConfigured(builder.Configuration));

    // Traces and metrics (OpenTelemetry); exported over OTLP when Observability:OtlpEndpoint is set.
    builder.Services.AddGcsObservability(builder.Configuration);

    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services
        .AddApiVersioning(options =>
        {
            options.DefaultApiVersion = ApiVersions.V1;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

    builder.Services.AddGcsReverseProxySupport();
    builder.Services.AddGcsSecurity();
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // Live push to operator clients. Registered after AddInfrastructure so it replaces the no-op publisher.
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<ILiveUpdatePublisher, SignalRLiveUpdatePublisher>();

    var app = builder.Build();

    app.UseGcsReverseProxySupport(); // behind Nginx: the client's address and scheme, not the proxy's (docs/deployment.md)
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    // HTTPS: in production the API runs behind a TLS-terminating proxy or with a configured certificate (docs/security.md).
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    // Order matters: who you are (authentication) → how often you may ask (rate limit, per user) → what you may do.
    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseAuthorization();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.MapHealthEndpoints();
    app.MapSystemEndpoints();
    app.MapVehicleEndpoints();
    app.MapMissionEndpoints();
    app.MapCommandEndpoints();
    app.MapNetworkEndpoints();
    app.MapAuthEndpoints();
    app.MapHub<TelemetryHub>(RealtimeRoutes.TelemetryHub).RequireAuthorization(Permissions.Read);
    app.MapHub<VehiclesHub>(RealtimeRoutes.VehiclesHub).RequireAuthorization(Permissions.Read);

    await app.Services.InitializeInfrastructureAsync(app.Lifetime.ApplicationStopping);
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "GCS API terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
