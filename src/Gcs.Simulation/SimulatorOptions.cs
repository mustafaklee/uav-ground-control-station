using System.ComponentModel.DataAnnotations;

namespace Gcs.Simulation;

public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Host name or IP of the GCS (in docker-compose: the API service name).</summary>
    [Required]
    public string GcsHost { get; init; } = "127.0.0.1";

    /// <summary>UDP port the GCS listens on. 14550 is the conventional GCS port (PX4 SITL sends there too).</summary>
    [Range(1, 65535)]
    public int GcsPort { get; init; } = 14550;

    /// <summary>MAVLink system id of the simulated vehicle; register the vehicle in the GCS with the same id.</summary>
    [Range(1, 255)]
    public int SystemId { get; init; } = 1;

    public double CenterLatitude { get; init; } = 39.925533;

    public double CenterLongitude { get; init; } = 32.866287;

    [Range(10, 10_000)]
    public double OrbitRadiusMetres { get; init; } = 150;

    [Range(1, 100)]
    public double SpeedMetresPerSecond { get; init; } = 12;
}
