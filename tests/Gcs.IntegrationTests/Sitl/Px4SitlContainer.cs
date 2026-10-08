using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Gcs.IntegrationTests.Sitl;

/// <summary>
/// A real PX4 autopilot (SITL with SIH physics, headless) in a container, built from docker/px4-sitl like the compose
/// service. It sends MAVLink to the test API, which runs in this process on the host, through host.docker.internal.
/// </summary>
internal sealed class Px4SitlContainer : IAsyncDisposable
{
    private const string HostAlias = "host.docker.internal";

    private readonly IFutureDockerImage _image;
    private readonly IContainer _container;

    public Px4SitlContainer(int systemId, int gcsPort, double homeLatitude, double homeLongitude)
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(RepositoryRoot(), "docker", "px4-sitl"))
            .WithName("gcs-px4-sitl:test")
            .WithCleanUp(false) // keep the small image between runs; only the base image download is slow
            .Build();

        _container = new ContainerBuilder(_image)
            // Docker Desktop resolves host.docker.internal on its own; on Linux (CI) it has to be mapped to the host.
            .WithExtraHost(HostAlias, "host-gateway")
            .WithEnvironment("GCS_HOST", HostAlias)
            .WithEnvironment("GCS_PORT", gcsPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("PX4_PARAM_MAV_SYS_ID", systemId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("PX4_HOME_LAT", homeLatitude.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("PX4_HOME_LON", homeLongitude.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("PX4_HOME_ALT", "938")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Startup script returned successfully"))
            .Build();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _image.CreateAsync(cancellationToken);
        await _container.StartAsync(cancellationToken);
    }

    /// <summary>PX4's console output, attached to a failing test to explain what the autopilot refused and why.</summary>
    public async Task<string> GetLogsAsync()
    {
        var (stdout, stderr) = await _container.GetLogsAsync();
        return stdout + stderr;
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gcs.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Gcs.slnx not found above the test output folder.");
    }
}
