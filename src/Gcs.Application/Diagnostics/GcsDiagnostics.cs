using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gcs.Application.Diagnostics;

/// <summary>
/// Our own spans. ASP.NET Core, HttpClient and Npgsql already produce spans; these add the steps only we know about
/// (a MAVLink command and its retries, a mission transfer, an outbox publish) so one trace shows the whole chain:
/// <code>
/// HTTP POST /commands ─► INSERT command_audit ─► vehicle.command Arm (COMMAND_LONG ×n, ACK) ─► UPDATE command_audit
/// HTTP POST /vehicles ─► INSERT vehicles + outbox ─ ─ ─ (later) ─ ─ ─► outbox publish VehicleRegistered ─► RabbitMQ
/// </code>
/// System.Diagnostics types only: OpenTelemetry is wired by the host, the layers below never reference it.
/// </summary>
public static class GcsTracing
{
    public const string SourceName = "Gcs";

    public static readonly ActivitySource Source = new(SourceName, typeof(GcsTracing).Assembly.GetName().Version?.ToString());

    // Attribute names, kept in one place so dashboards and tests agree on them.
    public const string VehicleId = "gcs.vehicle.id";
    public const string Command = "gcs.command";
    public const string CommandOutcome = "gcs.command.outcome";
    public const string CommandAttempts = "gcs.command.attempts";
    public const string CorrelationId = "gcs.correlation_id";
}

/// <summary>
/// Business metrics under the meter "Gcs". Created through <see cref="IMeterFactory"/>, so every host (and every test
/// host) has its own instruments. Recording costs almost nothing when nobody listens.
/// </summary>
public sealed class GcsMetrics : IDisposable
{
    public const string MeterName = "Gcs";

    private readonly Meter _meter;

    public GcsMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        Commands = _meter.CreateCounter<long>("gcs.commands", description: "Vehicle commands by command and outcome.");
        CommandDuration = _meter.CreateHistogram<double>(
            "gcs.command.duration", unit: "s", description: "Time from sending a command until its final answer or timeout.");
        MissionTransfers = _meter.CreateCounter<long>("gcs.mission.transfers", description: "Mission uploads and downloads by result.");
        MavlinkFrames = _meter.CreateCounter<long>("gcs.mavlink.frames", description: "Valid MAVLink frames received from vehicles.");
        LinkStateChanges = _meter.CreateCounter<long>("gcs.link.state_changes", description: "Vehicle link state transitions by new state.");
        OutboxPublished = _meter.CreateCounter<long>("gcs.outbox.published", description: "Events delivered to the broker.");
        OutboxFailures = _meter.CreateCounter<long>("gcs.outbox.failures", description: "Failed broker publish attempts.");
        Logins = _meter.CreateCounter<long>("gcs.auth.logins", description: "Sign-in attempts by result.");
    }

    /// <summary>For code paths constructed without dependency injection (unit tests, tools).</summary>
    public static GcsMetrics Unobserved { get; } = new(new StandaloneMeterFactory());

    public Counter<long> Commands { get; }

    public Histogram<double> CommandDuration { get; }

    public Counter<long> MissionTransfers { get; }

    public Counter<long> MavlinkFrames { get; }

    public Counter<long> LinkStateChanges { get; }

    public Counter<long> OutboxPublished { get; }

    public Counter<long> OutboxFailures { get; }

    public Counter<long> Logins { get; }

    /// <summary>Registers a gauge read on every collection, e.g. "links per state" from the link manager.</summary>
    public void ObserveGauge(string name, Func<IEnumerable<Measurement<int>>> observe, string description) =>
        _meter.CreateObservableGauge(name, observe, description: description);

    public void Dispose() => _meter.Dispose();

    private sealed class StandaloneMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
