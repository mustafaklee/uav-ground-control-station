using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
using Gcs.Application.Diagnostics;
using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Transports;
using Gcs.Mavlink.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Mavlink.Connections;

/// <summary>
/// Owns one <see cref="MavlinkConnection"/> per vehicle. Singleton: links outlive HTTP requests.
/// On shutdown every link is closed cleanly.
/// </summary>
internal sealed class VehicleLinkManager(
    IMavlinkTransportFactory transports,
    ITelemetrySink telemetry,
    IVehicleLinkEventSink events,
    IOptions<MavlinkConnectionOptions> options,
    TimeProvider time,
    ILoggerFactory loggers,
    GcsMetrics metrics) : IVehicleLinkManager, IVehicleMissionTransfer, IVehicleCommandSender, IAsyncDisposable
{
    private static readonly Error AlreadyConnected = Error.Conflict(
        "vehicle.link.already_active",
        "The vehicle already has an active link. Disconnect it first to change settings.");

    private readonly ConcurrentDictionary<VehicleId, MavlinkConnection> _connections = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _gaugeRegistered;

    public async Task<Result> ConnectAsync(VehicleLinkTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Serialized so two simultaneous "connect" requests cannot both create a link for the same vehicle.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connections.TryGetValue(target.VehicleId, out var existing))
            {
                if (existing.State is not (ConnectionState.Faulted or ConnectionState.Disconnected))
                {
                    return AlreadyConnected;
                }

                // A faulted link is replaced: "connect" on a faulted vehicle is the operator's manual retry.
                _connections.TryRemove(target.VehicleId, out _);
                await existing.DisposeAsync();
            }

            RegisterGauge();
            var connection = new MavlinkConnection(
                target, transports, telemetry, events, options.Value, time, loggers.CreateLogger<MavlinkConnection>(), metrics: metrics);
            _connections[target.VehicleId] = connection;
            connection.Start();
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(VehicleId vehicleId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connections.TryRemove(vehicleId, out var connection))
            {
                await connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<VehicleLinkStatus> GetAll() => [.. _connections.Values.Select(c => c.GetStatus())];

    /// <summary>"How many links are in each state", read by the metrics collector (gauge gcs.links).</summary>
    private void RegisterGauge()
    {
        if (Interlocked.Exchange(ref _gaugeRegistered, 1) == 0)
        {
            metrics.ObserveGauge(
                "gcs.links",
                () => GetAll().GroupBy(s => s.State).Select(g => new System.Diagnostics.Metrics.Measurement<int>(
                    g.Count(), new KeyValuePair<string, object?>("state", g.Key.ToString()))),
                "Vehicle links per state.");

            // Link quality per vehicle (ADR-019): the dashboard and any OTLP backend can graph and alert on these.
            metrics.ObserveGauge("gcs.link.rtt", () => QualityGauge(q => q.RoundTripMilliseconds), "ms", "Smoothed TIMESYNC round-trip time per vehicle link.");
            metrics.ObserveGauge("gcs.link.packet_loss", () => QualityGauge(q => q.RecentPacketLossRatio), "1", "Packet loss over the last 10 s per vehicle link.");
            metrics.ObserveGauge("gcs.link.message_rate", () => QualityGauge(q => q.MessagesPerSecond), "{message}/s", "Messages per second per vehicle link.");
            metrics.ObserveGauge("gcs.link.radio.rssi", () => QualityGauge(q => q.Radio?.Rssi), "1", "Telemetry radio RSSI (radio units) per vehicle link.");
        }
    }

    private IEnumerable<System.Diagnostics.Metrics.Measurement<double>> QualityGauge(Func<LinkQuality, double?> value) =>
        GetAll()
            .Where(s => s.State == ConnectionState.Connected)
            .Select(s => (s.VehicleId, Value: value(s.Quality)))
            .Where(m => m.Value is not null)
            .Select(m => new System.Diagnostics.Metrics.Measurement<double>(
                m.Value!.Value, new KeyValuePair<string, object?>(GcsTracing.VehicleId, m.VehicleId.Value.ToString())));

    /// <summary>Every link with what it targets, for the network topology.</summary>
    internal IReadOnlyList<(VehicleLinkTarget Target, VehicleLinkStatus Status)> GetLinks() =>
        [.. _connections.Values.Select(c => (c.Target, c.GetStatus()))];

    public VehicleLinkStatus? GetStatus(VehicleId vehicleId) =>
        _connections.TryGetValue(vehicleId, out var connection) ? connection.GetStatus() : null;

    public async Task<Result> UploadAsync(VehicleId vehicleId, IReadOnlyList<MissionItem> items, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(vehicleId, out var connection))
        {
            return MavlinkConnection.NotConnected;
        }

        var target = connection.Target;
        var here = connection.LastPosition;
        if (here is null && MissionItemMapper.NeedsVehiclePosition(items))
        {
            return MavlinkConnection.PositionUnknown;
        }

        var messages = MissionItemMapper.ToMavlink(items, target.Autopilot, target.SystemId.Value, MavComponent.Autopilot1, here);
        return await connection.UploadMissionAsync(messages, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<MissionItem>>> DownloadAsync(VehicleId vehicleId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(vehicleId, out var connection))
        {
            return MavlinkConnection.NotConnected;
        }

        var download = await connection.DownloadMissionAsync(cancellationToken);
        return download.IsSuccess
            ? Result.Success(MissionItemMapper.FromMavlink(download.Value, connection.Target.Autopilot))
            : download.Error;
    }

    public Task<CommandDelivery> SendAsync(VehicleId vehicleId, VehicleCommand command, CancellationToken cancellationToken) =>
        _connections.TryGetValue(vehicleId, out var connection)
            ? connection.SendCommandAsync(command, cancellationToken)
            : Task.FromResult(new CommandDelivery(CommandDeliveryStatus.NotConnected, 0, null));

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        _gate.Dispose();
    }
}
