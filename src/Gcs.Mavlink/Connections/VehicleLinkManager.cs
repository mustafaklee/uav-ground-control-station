using System.Collections.Concurrent;
using Gcs.Application.Abstractions;
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
    ILoggerFactory loggers) : IVehicleLinkManager, IVehicleMissionTransfer, IAsyncDisposable
{
    private static readonly Error AlreadyConnected = Error.Conflict(
        "vehicle.link.already_active",
        "The vehicle already has an active link. Disconnect it first to change settings.");

    private readonly ConcurrentDictionary<VehicleId, MavlinkConnection> _connections = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

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

            var connection = new MavlinkConnection(
                target, transports, telemetry, events, options.Value, time, loggers.CreateLogger<MavlinkConnection>());
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

    public VehicleLinkStatus? GetStatus(VehicleId vehicleId) =>
        _connections.TryGetValue(vehicleId, out var connection) ? connection.GetStatus() : null;

    public async Task<Result> UploadAsync(VehicleId vehicleId, IReadOnlyList<MissionItem> items, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(vehicleId, out var connection))
        {
            return MavlinkConnection.NotConnected;
        }

        var target = connection.Target;
        var messages = MissionItemMapper.ToMavlink(items, target.Autopilot, target.SystemId.Value, MavComponent.Autopilot1);
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
