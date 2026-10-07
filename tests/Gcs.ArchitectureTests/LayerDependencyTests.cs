using System.Reflection;
using NetArchTest.Rules;

namespace Gcs.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency rule: source code dependencies point inwards only.
/// A failing test here means a layer started to know about something it should not (e.g. Domain about EF Core).
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void Domain_depends_on_nothing_else_in_the_solution_or_on_frameworks() =>
        AssertNoDependency(Layers.DomainAssembly,
            Layers.Application, Layers.Contracts, Layers.Mavlink, Layers.Telemetry, Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop,
            Layers.EntityFrameworkCore, Layers.Npgsql, Layers.RabbitMq, Layers.AspNetCore, Layers.Avalonia);

    [Fact]
    public void Contracts_are_standalone() =>
        AssertNoDependency(Layers.ContractsAssembly,
            Layers.Domain, Layers.Application, Layers.Mavlink, Layers.Telemetry, Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop,
            Layers.EntityFrameworkCore, Layers.RabbitMq, Layers.AspNetCore, Layers.Avalonia);

    [Fact]
    public void Application_does_not_depend_on_adapters_or_hosts() =>
        AssertNoDependency(Layers.ApplicationAssembly,
            Layers.Mavlink, Layers.Telemetry, Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop,
            Layers.EntityFrameworkCore, Layers.Npgsql, Layers.RabbitMq, Layers.AspNetCore, Layers.Avalonia);

    [Fact]
    public void Persistence_does_not_depend_on_other_adapters_or_hosts() =>
        AssertNoDependency(Layers.PersistenceAssembly,
            Layers.Mavlink, Layers.Telemetry, Layers.Messaging, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop, Layers.RabbitMq, Layers.AspNetCore);

    [Fact]
    public void Messaging_does_not_depend_on_other_adapters_or_hosts() =>
        AssertNoDependency(Layers.MessagingAssembly,
            Layers.Mavlink, Layers.Telemetry, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop, Layers.EntityFrameworkCore, Layers.AspNetCore);

    [Fact]
    public void Mavlink_does_not_depend_on_storage_messaging_or_hosts() =>
        AssertNoDependency(Layers.MavlinkAssembly,
            Layers.Telemetry, Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop, Layers.EntityFrameworkCore, Layers.RabbitMq, Layers.AspNetCore);

    [Fact]
    public void Telemetry_does_not_depend_on_storage_messaging_or_hosts() =>
        AssertNoDependency(Layers.TelemetryAssembly,
            Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api, Layers.Desktop, Layers.EntityFrameworkCore, Layers.RabbitMq, Layers.AspNetCore);

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts() =>
        AssertNoDependency(Layers.InfrastructureAssembly, Layers.Simulation, Layers.Api, Layers.Desktop, Layers.AspNetCore);

    [Fact]
    public void Api_reaches_adapters_only_through_infrastructure() =>
        AssertNoDependency(Layers.ApiAssembly,
            Layers.Persistence, Layers.Messaging, Layers.Mavlink, Layers.Telemetry, Layers.Simulation, Layers.Desktop, Layers.EntityFrameworkCore, Layers.Npgsql, Layers.RabbitMq);

    [Fact]
    public void Desktop_knows_only_public_contracts() =>
        AssertNoDependency(Layers.DesktopAssembly,
        [
            Layers.Domain, Layers.Application, Layers.Mavlink, Layers.Telemetry, Layers.Messaging, Layers.Persistence, Layers.Infrastructure, Layers.Simulation, Layers.Api,
            Layers.EntityFrameworkCore, Layers.Npgsql, Layers.RabbitMq, .. Layers.AspNetCoreServer,
        ]);

    [Fact]
    public void Simulation_does_not_depend_on_the_backend() =>
        AssertNoDependency(Layers.SimulationAssembly,
            Layers.Application, Layers.Persistence, Layers.Messaging, Layers.Infrastructure, Layers.Telemetry, Layers.Api, Layers.Desktop, Layers.EntityFrameworkCore, Layers.RabbitMq);

    private static void AssertNoDependency(Assembly assembly, params string[] forbiddenNamespaces)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        var offenders = result.FailingTypeNames ?? [];
        result.IsSuccessful.ShouldBeTrue(
            $"{assembly.GetName().Name} must not depend on [{string.Join(", ", forbiddenNamespaces)}]. Offending types: {string.Join(", ", offenders)}");
    }
}
