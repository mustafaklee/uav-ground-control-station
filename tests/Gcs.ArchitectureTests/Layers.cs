using System.Reflection;

namespace Gcs.ArchitectureTests;

/// <summary>
/// Namespaces and assemblies of every layer, in one place so the rules read like the architecture diagram.
/// </summary>
internal static class Layers
{
    public const string Domain = "Gcs.Domain";
    public const string Application = "Gcs.Application";
    public const string Contracts = "Gcs.Contracts";
    public const string Mavlink = "Gcs.Mavlink";
    public const string Telemetry = "Gcs.Telemetry";
    public const string Messaging = "Gcs.Messaging";
    public const string Persistence = "Gcs.Persistence";
    public const string Infrastructure = "Gcs.Infrastructure";
    public const string Simulation = "Gcs.Simulation";
    public const string Api = "Gcs.Api";
    public const string Desktop = "Gcs.Desktop";

    public const string EntityFrameworkCore = "Microsoft.EntityFrameworkCore";
    public const string Npgsql = "Npgsql";
    public const string RabbitMq = "RabbitMQ";
    public const string AspNetCore = "Microsoft.AspNetCore";
    public const string Avalonia = "Avalonia";

    public static readonly Assembly DomainAssembly = Gcs.Domain.AssemblyReference.Assembly;
    public static readonly Assembly ApplicationAssembly = Gcs.Application.AssemblyReference.Assembly;
    public static readonly Assembly ContractsAssembly = Gcs.Contracts.AssemblyReference.Assembly;
    public static readonly Assembly MavlinkAssembly = Gcs.Mavlink.AssemblyReference.Assembly;
    public static readonly Assembly TelemetryAssembly = Gcs.Telemetry.AssemblyReference.Assembly;
    public static readonly Assembly MessagingAssembly = Gcs.Messaging.AssemblyReference.Assembly;
    public static readonly Assembly PersistenceAssembly = Gcs.Persistence.AssemblyReference.Assembly;
    public static readonly Assembly InfrastructureAssembly = Gcs.Infrastructure.AssemblyReference.Assembly;
    public static readonly Assembly ApiAssembly = typeof(Gcs.Api.Middleware.CorrelationIdMiddleware).Assembly;
    public static readonly Assembly DesktopAssembly = typeof(Gcs.Desktop.App).Assembly;
    public static readonly Assembly SimulationAssembly = Assembly.Load(Simulation);
}
