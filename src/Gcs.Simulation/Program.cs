using Gcs.Simulation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SimulatorHost>();

var host = builder.Build();
await host.RunAsync();
