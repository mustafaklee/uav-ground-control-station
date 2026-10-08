using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Gcs.Contracts.Network;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using Gcs.Mavlink.Simulation;
using Gcs.Mavlink.Transports;

namespace Gcs.IntegrationTests.Network;

/// <summary>
/// Phase 12 end to end: several vehicles send to one UDP port, as PX4 multi-vehicle SITL does. Each is connected and
/// measured on its own, a vehicle nobody registered shows up in the topology, and a telemetry radio appears as a node.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class MultiVehicleNetworkTests(GcsApiFactory factory)
{
    private const string Vehicles = "/api/v1/vehicles";
    private static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client = factory.CreateAdminClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vehicles_sharing_one_udp_port_are_linked_measured_and_shown_in_the_topology()
    {
        var port = FreeUdpPort();
        var alpha = await RegisterAsync(port);
        var bravo = await RegisterAsync(port);
        var strangerSystemId = (byte)TestData.NextSystemId(); // switched on, never registered

        using var stop = new CancellationTokenSource();
        var simulators = new[]
        {
            Simulate((byte)alpha.MavlinkSystemId, port, radio: false, stop.Token),
            Simulate((byte)bravo.MavlinkSystemId, port, radio: true, stop.Token),
            Simulate(strangerSystemId, port, radio: false, stop.Token),
        };

        try
        {
            await ConnectAsync(alpha.Id);
            await ConnectAsync(bravo.Id);

            // Each link measures itself: TIMESYNC round trip on both, radio status only where a radio reports.
            var alphaLink = await WaitForLinkAsync(alpha.Id, l => l.Quality.RoundTripMilliseconds is not null, "alpha's round trip is measured");
            var bravoLink = await WaitForLinkAsync(bravo.Id, l => l.Quality.Radio is not null, "bravo's radio reports");
            alphaLink.Quality.Grade.ShouldBe("Good");
            alphaLink.Quality.RoundTripMilliseconds!.Value.ShouldBeLessThan(500);
            alphaLink.Quality.MessagesPerSecond.ShouldBeGreaterThan(5);
            alphaLink.Quality.Radio.ShouldBeNull();
            bravoLink.Quality.Radio!.Rssi.ShouldBeInRange(20, 200);

            // Telemetry is not mixed up between the two: each reports its own system's position stream.
            (await TelemetryAsync(alpha.Id)).ShouldNotBeNull();
            (await TelemetryAsync(bravo.Id)).ShouldNotBeNull();

            var topology = await Eventually.GetAsync(
                async () => (await _client.GetFromJsonAsync<NetworkTopologyResponse>("/api/v1/network/topology", Ct))!,
                t => t.Endpoints.Any(e => e.UnregisteredSystems.Any(s => s.SystemId == strangerSystemId)),
                LinkTimeout,
                "the stranger is heard");
            var endpoint = topology.Endpoints.Single(e => e.Id == $"udp://127.0.0.1:{port}");
            endpoint.Transport.ShouldBe("Udp");
            endpoint.Vehicles.Select(v => v.VehicleId).ShouldBe([alpha.Id, bravo.Id], ignoreOrder: true);
            endpoint.Vehicles.ShouldAllBe(v => v.State == "Connected" && v.Remote != null);
            endpoint.UnregisteredSystems.Select(s => s.SystemId).ShouldContain(strangerSystemId);
            var radio = topology.Radios.Single(r => r.VehicleId == bravo.Id);
            radio.Kind.ShouldBe("mavlink-radio");
            radio.Endpoint.ShouldBe(endpoint.Id);
            radio.Neighbours.ShouldHaveSingleItem().Rssi.ShouldBe(radio.Status.Rssi);
        }
        finally
        {
            await _client.DeleteAsync(new Uri($"{Vehicles}/{alpha.Id}/connection", UriKind.Relative), Ct);
            await _client.DeleteAsync(new Uri($"{Vehicles}/{bravo.Id}/connection", UriKind.Relative), Ct);
            await stop.CancelAsync();
            await Task.WhenAll(simulators);
        }
    }

    private static Task Simulate(byte systemId, int port, bool radio, CancellationToken stop)
    {
        var runner = new SimulatedVehicleRunner(
            new SimulatedVehicle(new SimulatedVehicleOptions { SystemId = systemId, SimulateRadio = radio }),
            UdpMavlinkTransport.Connect(new IPEndPoint(IPAddress.Loopback, port)),
            TimeProvider.System);
        return Task.Run(() => runner.RunAsync(stop), CancellationToken.None);
    }

    private async Task<VehicleResponse> RegisterAsync(int port)
    {
        var request = TestData.Registration() with { Connection = new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: port) };
        var response = await _client.PostAsJsonAsync(Vehicles, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    private async Task ConnectAsync(Guid id)
    {
        var response = await _client.PostAsync(new Uri($"{Vehicles}/{id}/connection", UriKind.Relative), null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<VehicleLinkResponse> WaitForLinkAsync(Guid id, Func<VehicleLinkResponse, bool> condition, string because) =>
        Eventually.GetAsync(
            async () => (await _client.GetFromJsonAsync<VehicleLinkResponse>($"{Vehicles}/{id}/connection", Ct))!,
            l => l.State == "Connected" && condition(l),
            LinkTimeout,
            because);

    private Task<TelemetryResponse?> TelemetryAsync(Guid id) =>
        Eventually.GetAsync(
            async () =>
            {
                var response = await _client.GetAsync(new Uri($"{Vehicles}/{id}/telemetry", UriKind.Relative), Ct);
                return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TelemetryResponse>(Ct) : null;
            },
            t => t?.Position is not null,
            LinkTimeout,
            "position arrives");

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
