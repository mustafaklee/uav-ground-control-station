using Gcs.Simulation;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<SimulatorOptions>()
    .BindConfiguration(SimulatorOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SimulatorHost>();

var host = builder.Build();
await host.RunAsync();
