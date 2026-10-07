using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Transports;

namespace Gcs.Mavlink.Simulation;

/// <summary>
/// Runs a <see cref="SimulatedVehicle"/> on a transport: telemetry at 10 Hz, heartbeat at 1 Hz, replies to commands.
/// The same runner powers the in-process simulator transport and the standalone simulator over UDP.
/// </summary>
public sealed class SimulatedVehicleRunner(SimulatedVehicle vehicle, IMavlinkTransport transport, TimeProvider time)
{
    public static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(100);
    private const int TicksPerHeartbeat = 10;
    private const int ReceiveBufferSize = 2048;

    private byte _sequence;

    /// <summary>When true the vehicle stays alive but sends nothing, like a radio that lost its link.</summary>
    public bool IsSilent { get; set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await transport.OpenAsync(cancellationToken);
        var receiving = ReceiveLoopAsync(cancellationToken);

        try
        {
            using var timer = new PeriodicTimer(TelemetryInterval, time);
            var tick = 0;
            await SendAsync(vehicle.Heartbeat(), cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                vehicle.Step(TelemetryInterval);
                if (++tick % TicksPerHeartbeat == 0)
                {
                    await SendAsync(vehicle.Heartbeat(), cancellationToken);
                }

                foreach (var message in vehicle.Telemetry())
                {
                    await SendAsync(message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }

        await receiving;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var parser = new MavlinkFrameParser();
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await transport.ReceiveAsync(buffer, cancellationToken);
                foreach (var frame in parser.Parse(buffer.AsSpan(0, read)))
                {
                    if (MavlinkCodec.TryDecode(frame, out var message) && vehicle.Handle(message!) is { } reply)
                    {
                        await SendAsync(reply, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private ValueTask SendAsync(Protocol.Messages.IMavlinkMessage message, CancellationToken cancellationToken)
    {
        if (IsSilent)
        {
            return ValueTask.CompletedTask;
        }

        var frame = MavlinkCodec.Encode(message, _sequence++, vehicle.Options.SystemId, MavComponent.Autopilot1);
        return transport.SendAsync(frame, cancellationToken);
    }
}
