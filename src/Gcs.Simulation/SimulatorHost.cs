using System.Net;
using System.Net.Sockets;
using Gcs.Mavlink.Simulation;
using Gcs.Mavlink.Transports;
using Microsoft.Extensions.Options;

namespace Gcs.Simulation;

/// <summary>
/// Runs one simulated vehicle that sends MAVLink over UDP to the GCS, exactly like PX4 SITL would.
/// If the GCS host cannot be resolved yet (containers starting in parallel), it waits and tries again.
/// </summary>
internal sealed partial class SimulatorHost(IOptions<SimulatorOptions> options, TimeProvider time, ILogger<SimulatorHost> logger)
    : BackgroundService
{
    private static readonly TimeSpan ResolveRetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var gcs = await ResolveAsync(settings, stoppingToken);
        if (gcs is null)
        {
            return;
        }

        var vehicle = new SimulatedVehicle(new SimulatedVehicleOptions
        {
            SystemId = (byte)settings.SystemId,
            CenterLatitude = settings.CenterLatitude,
            CenterLongitude = settings.CenterLongitude,
            OrbitRadiusMetres = settings.OrbitRadiusMetres,
            SpeedMetresPerSecond = settings.SpeedMetresPerSecond,
            StartAirborne = settings.StartAirborne,
            SimulateRadio = settings.SimulateRadio,
        });

        await using var transport = UdpMavlinkTransport.Connect(gcs);
        LogStarted(settings.SystemId, gcs);
        await new SimulatedVehicleRunner(vehicle, transport, time).RunAsync(stoppingToken);
    }

    private async Task<IPEndPoint?> ResolveAsync(SimulatorOptions settings, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(settings.GcsHost, AddressFamily.InterNetwork, stoppingToken);
                if (addresses.Length > 0)
                {
                    return new IPEndPoint(addresses[0], settings.GcsPort);
                }
            }
            catch (SocketException ex)
            {
                LogResolveFailed(settings.GcsHost, ex.Message);
            }

            try
            {
                await Task.Delay(ResolveRetryDelay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulated vehicle (system id {SystemId}) sending MAVLink to udp://{Gcs}")]
    private partial void LogStarted(int systemId, IPEndPoint gcs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot resolve GCS host {Host} yet ({Reason}); retrying")]
    private partial void LogResolveFailed(string host, string reason);
}
