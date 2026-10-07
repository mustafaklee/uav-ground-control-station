using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using Gcs.Mavlink.Simulation;
using Gcs.Mavlink.Transports;

namespace Gcs.IntegrationTests.Vehicles;

/// <summary>
/// The MAVLink link end to end through the HTTP API: register → connect → heartbeat → telemetry → link loss → recovery.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class VehicleLinkTests(GcsApiFactory factory)
{
    private const string BasePath = "/api/v1/vehicles";
    private static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Simulator_vehicle_connects_and_streams_telemetry()
    {
        var vehicle = await RegisterAsync(new ConnectionSettingsDto("Simulator"));

        var connect = await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
        connect.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForStateAsync(vehicle.Id, "Connected");
        var telemetry = await Eventually.GetAsync(
            () => GetTelemetryAsync(vehicle.Id),
            t => t is { Position: not null, Flight: not null, Battery: not null, Gps: not null, Attitude: not null, Motion: not null },
            LinkTimeout,
            "every telemetry part has been received");

        telemetry!.Position!.Latitude.ShouldBe(39.9255, tolerance: 0.01);
        telemetry.Position.RelativeAltitude.ShouldBe(100, tolerance: 0.1);
        telemetry.Flight.ShouldBe(new FlightDto(Armed: true, "AUTO.LOITER"));
        telemetry.Gps!.Fix.ShouldBe("Fix3D");
        telemetry.Motion!.GroundSpeed.ShouldBe(12, tolerance: 0.1);
        telemetry.Battery!.RemainingPercent.ShouldNotBeNull();

        (await _client.DeleteAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetLinkAsync(vehicle.Id)).State.ShouldBe("Disconnected");
    }

    [Fact]
    public async Task Udp_link_detects_loss_gives_up_after_bounded_retries_and_recovers_on_manual_connect()
    {
        var port = FreeUdpPort();
        var vehicle = await RegisterAsync(new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: port));

        // A simulated vehicle on the other side of a real UDP socket, like PX4 SITL sending to the GCS.
        using var simulatorStop = new CancellationTokenSource();
        var runner = new SimulatedVehicleRunner(
            new SimulatedVehicle(new SimulatedVehicleOptions { SystemId = (byte)vehicle.MavlinkSystemId }),
            UdpMavlinkTransport.Connect(new IPEndPoint(IPAddress.Loopback, port)),
            TimeProvider.System);
        var simulator = Task.Run(() => runner.RunAsync(simulatorStop.Token), Ct);

        try
        {
            await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
            await WaitForStateAsync(vehicle.Id, "Connected");
            (await Eventually.GetAsync(() => GetTelemetryAsync(vehicle.Id), t => t?.Position is not null, LinkTimeout, "position arrives"))
                .ShouldNotBeNull();

            // Radio link lost: the vehicle stops talking.
            runner.IsSilent = true;
            await WaitForStateAsync(vehicle.Id, "Reconnecting");
            var faulted = await WaitForStateAsync(vehicle.Id, "Faulted");
            faulted.ReconnectAttempts.ShouldBe(2);
            faulted.FaultReason.ShouldNotBeNullOrWhiteSpace();

            // Link is back; the operator retries.
            runner.IsSilent = false;
            var retry = await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
            retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var connected = await WaitForStateAsync(vehicle.Id, "Connected");
            connected.Quality.FramesReceived.ShouldBeGreaterThan(0);
        }
        finally
        {
            await _client.DeleteAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), Ct);
            await simulatorStop.CancelAsync();
            await simulator;
        }
    }

    [Fact]
    public async Task Connecting_twice_is_rejected_while_the_link_is_active()
    {
        var vehicle = await RegisterAsync(new ConnectionSettingsDto("Simulator"));
        await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);

        var second = await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(second)).ShouldBe("vehicle.link.already_active");
        await _client.DeleteAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task Retiring_a_connected_vehicle_closes_its_link_and_it_cannot_be_reconnected()
    {
        var vehicle = await RegisterAsync(new ConnectionSettingsDto("Simulator"));
        await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
        await WaitForStateAsync(vehicle.Id, "Connected");

        await _client.DeleteAsync(new Uri($"{BasePath}/{vehicle.Id}", UriKind.Relative), Ct);
        var reconnect = await _client.PostAsync(new Uri($"{BasePath}/{vehicle.Id}/connection", UriKind.Relative), null, Ct);

        (await GetLinkAsync(vehicle.Id)).State.ShouldBe("Disconnected");
        reconnect.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(reconnect)).ShouldBe("vehicle.retired");
    }

    [Fact]
    public async Task Telemetry_of_a_never_connected_vehicle_is_not_available()
    {
        var vehicle = await RegisterAsync(new ConnectionSettingsDto("Simulator"));

        var response = await _client.GetAsync(new Uri($"{BasePath}/{vehicle.Id}/telemetry", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemCodeAsync(response)).ShouldBe("telemetry.not_available");
        (await GetLinkAsync(vehicle.Id)).State.ShouldBe("Disconnected");
    }

    [Fact]
    public async Task Link_endpoints_return_404_for_unknown_vehicles()
    {
        var id = Guid.NewGuid();

        (await _client.PostAsync(new Uri($"{BasePath}/{id}/connection", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync(new Uri($"{BasePath}/{id}/connection", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync(new Uri($"{BasePath}/{id}/telemetry", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<VehicleResponse> RegisterAsync(ConnectionSettingsDto connection)
    {
        var request = TestData.Registration() with { Connection = connection };
        var response = await _client.PostAsJsonAsync(BasePath, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    private Task<VehicleLinkResponse> WaitForStateAsync(Guid id, string state) =>
        Eventually.GetAsync(() => GetLinkAsync(id), link => link.State == state, LinkTimeout, $"link state is {state}");

    private async Task<VehicleLinkResponse> GetLinkAsync(Guid id) =>
        (await _client.GetFromJsonAsync<VehicleLinkResponse>($"{BasePath}/{id}/connection", Ct))!;

    private async Task<TelemetryResponse?> GetTelemetryAsync(Guid id)
    {
        var response = await _client.GetAsync(new Uri($"{BasePath}/{id}/telemetry", UriKind.Relative), Ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TelemetryResponse>(Ct) : null;
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
        return problem.RootElement.GetProperty("code").GetString();
    }
}
