using Asp.Versioning;
using Gcs.Api;
using Gcs.Api.Endpoints;
using Gcs.Api.Middleware;
using Gcs.Application;
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
        .Enrich.FromLogContext());

    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services
        .AddApiVersioning(options =>
        {
            options.DefaultApiVersion = ApiVersions.V1;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    var app = builder.Build();

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.MapHealthEndpoints();
    app.MapSystemEndpoints();
    app.MapVehicleEndpoints();

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
