using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Common;
using Gcs.Contracts.Missions;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Sitl;

/// <summary>
/// A whole flight with a real PX4 autopilot: connect over MAVLink/UDP, arm, take off, upload and fly a mission,
/// return to launch and land. Everything goes through the public HTTP API, exactly as the desktop client does it.
/// </summary>
/// <remarks>
/// Opt-in, because it downloads the PX4 image and flies for about two minutes:
/// <code>GCS_PX4_SITL=1 dotnet test --project tests/Gcs.IntegrationTests -- --filter-trait "Category=Sitl"</code>
/// CI runs it in its own job (see .github/workflows/ci.yml and docs/px4-sitl.md).
/// </remarks>
[Collection(ApiTestGroup.Name)]
[Trait("Category", "Sitl")]
public sealed class Px4SitlFlightTests(GcsApiFactory factory)
{
    public const string OptInVariable = "GCS_PX4_SITL";

    private const string Vehicles = "/api/v1/vehicles";
    private const double HomeLatitude = 39.925533;
    private const double HomeLongitude = 32.866287;
    private const double TakeoffAltitude = 20;
    private const double MetresPerDegree = 111_320;

    private readonly HttpClient _client = factory.CreateAdminClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Px4_takes_off_flies_a_mission_and_returns_to_launch()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OptInVariable) == "1",
            $"PX4 SITL flight test is opt-in: set {OptInVariable}=1 (needs Docker).");

        var systemId = TestData.NextSystemId();
        var port = FreeUdpPort();
        await using var px4 = new Px4SitlContainer(systemId, port, HomeLatitude, HomeLongitude);
        await px4.StartAsync(Ct);

        try
        {
            await FlyAsync(systemId, port);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("PX4 log:\n" + await px4.GetLogsAsync());
            throw;
        }
    }

    private async Task FlyAsync(int systemId, int port)
    {
        // 1. Register and connect. PX4 sends to the port the GCS listens on, as with QGroundControl's "udpin".
        var vehicle = await PostAsync<VehicleResponse>(Vehicles, new RegisterVehicleRequest(
            TestData.NextCallsign("PX4"), systemId, "Px4", "Multirotor", new ConnectionSettingsDto("Udp", Host: "0.0.0.0", Port: port)));
        var api = $"{Vehicles}/{vehicle.Id}";
        (await _client.PostAsync(new Uri($"{api}/connection", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Eventually.GetAsync(() => GetAsync<VehicleLinkResponse>($"{api}/connection"), l => l.State == "Connected", TimeSpan.FromSeconds(30), "PX4 is connected");
        var ready = await Eventually.GetAsync(
            () => GetAsync<TelemetryResponse>($"{api}/telemetry"),
            t => t is { Position: not null, Gps.Fix: "Fix3D", Motion: not null },
            TimeSpan.FromSeconds(30),
            "PX4 reports a 3D GPS fix, position and motion");
        ready.Position!.Latitude.ShouldBe(HomeLatitude, tolerance: 0.0001);
        ready.Motion!.AirSpeed.ShouldBeNull(); // no air speed sensor on a quad: PX4 sends NaN, the API says "unknown"

        // 2. Take control, arm, take off. Pre-arm checks pass a few seconds after boot, once the estimator converged.
        (await _client.PostAsync(new Uri($"{api}/command-lease", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SendUntilAcceptedAsync(api, new SendCommandRequest("Arm", Confirm: true), TimeSpan.FromSeconds(60));
        (await SendAsync(api, new SendCommandRequest("Takeoff", Altitude: TakeoffAltitude, Confirm: true))).ShouldBe(HttpStatusCode.OK);
        await WaitForTelemetryAsync(api, t => t.Position!.RelativeAltitude > TakeoffAltitude - 2, TimeSpan.FromSeconds(60), "the vehicle climbed to the takeoff altitude");

        // 3. A box mission about 100 m on each side. The takeoff item has no position ("here").
        var north = HomeLatitude + (100 / MetresPerDegree);
        var east = HomeLongitude + (100 / (MetresPerDegree * Math.Cos(HomeLatitude * Math.PI / 180)));
        var mission = await PostAsync<MissionResponse>("/api/v1/missions", new SaveMissionRequest("SITL box",
        [
            new("Takeoff", Altitude: TakeoffAltitude),
            new("Waypoint", north, HomeLongitude, 25),
            new("Waypoint", north, east, 25),
            new("Waypoint", HomeLatitude, east, 25),
            new("ReturnToLaunch"),
        ]));
        var upload = await _client.PostAsJsonAsync($"/api/v1/missions/{mission.Id}/upload", new UploadMissionRequest(vehicle.Id), Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));

        // The takeoff "here" must reach PX4 as real coordinates, never 0/0 (PX4 would fly towards 0° N 0° E).
        var onVehicle = await GetAsync<VehicleMissionResponse>($"{api}/mission");
        onVehicle.Items.Count.ShouldBe(5);
        onVehicle.Items[0].Latitude!.Value.ShouldBe(HomeLatitude, tolerance: 0.0001);
        onVehicle.Items[0].Longitude!.Value.ShouldBe(HomeLongitude, tolerance: 0.0001);

        // 4. Fly it. The vehicle must head north towards the first waypoint.
        // PX4 checks a new mission for a moment after the upload and refuses AUTO.MISSION until the check is done.
        await SendUntilAcceptedAsync(api, new SendCommandRequest("SetMode", Mode: "AUTO.MISSION", Confirm: true), TimeSpan.FromSeconds(15));
        var enRoute = await WaitForTelemetryAsync(
            api, t => DistanceFromHome(t.Position!) > 60, TimeSpan.FromSeconds(60), "the vehicle is 60 m from home on the mission");
        enRoute.Flight!.FlightMode.ShouldBe("AUTO.MISSION");
        enRoute.Position!.Latitude.ShouldBeGreaterThan(HomeLatitude);

        // 5. The operator calls it back: RTL, then PX4 lands at home and disarms itself.
        (await SendAsync(api, new SendCommandRequest("ReturnToLaunch"))).ShouldBe(HttpStatusCode.OK);
        await WaitForTelemetryAsync(api, t => t.Flight!.FlightMode == "AUTO.RTL", TimeSpan.FromSeconds(10), "PX4 is returning");
        var landed = await WaitForTelemetryAsync(api, t => !t.Flight!.Armed, TimeSpan.FromSeconds(150), "PX4 landed and disarmed");
        DistanceFromHome(landed.Position!).ShouldBeLessThan(5);
        landed.Position!.RelativeAltitude.ShouldBe(0, tolerance: 1);

        // Every command is on record as accepted by the vehicle, and the link was clean.
        var audit = await GetAsync<PagedResponse<CommandAuditResponse>>($"{api}/commands?pageSize=50");
        var accepted = audit.Items.Where(a => a.Outcome == "Accepted").Select(a => a.Command).ToList();
        accepted.ShouldBe(["ReturnToLaunch", "SetMode", "Takeoff", "Arm"]); // newest first
        var link = await GetAsync<VehicleLinkResponse>($"{api}/connection");
        link.Quality.PacketLossRatio.ShouldBeLessThan(0.05);
        link.Quality.CrcErrors.ShouldBe(0);
    }

    /// <summary>
    /// Sends again every 2 s while PX4 refuses (409 command.rejected), like an operator who waits for the vehicle to be
    /// ready. Refusals stay in the audit log as Rejected.
    /// </summary>
    private async Task SendUntilAcceptedAsync(string api, SendCommandRequest command, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpStatusCode status;
        while ((status = await SendAsync(api, command)) != HttpStatusCode.OK)
        {
            status.ShouldBe(HttpStatusCode.Conflict, $"{command.Command} failed for another reason than a refusal");
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"PX4 did not accept {command.Command} within {timeout.TotalSeconds} s.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }
    }

    private async Task<TelemetryResponse> WaitForTelemetryAsync(string api, Func<TelemetryResponse, bool> condition, TimeSpan timeout, string because) =>
        await Eventually.GetAsync(
            () => GetAsync<TelemetryResponse>($"{api}/telemetry"),
            t => t is { Position: not null, Flight: not null } && condition(t),
            timeout,
            because);

    private async Task<HttpStatusCode> SendAsync(string api, SendCommandRequest command)
    {
        var response = await _client.PostAsJsonAsync($"{api}/commands", command, Ct);
        return response.StatusCode;
    }

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    /// <summary>Flat-earth distance in metres; accurate to well under a metre over a few hundred metres.</summary>
    private static double DistanceFromHome(PositionDto position)
    {
        var north = (position.Latitude - HomeLatitude) * MetresPerDegree;
        var east = (position.Longitude - HomeLongitude) * MetresPerDegree * Math.Cos(HomeLatitude * Math.PI / 180);
        return Math.Sqrt((north * north) + (east * east));
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
