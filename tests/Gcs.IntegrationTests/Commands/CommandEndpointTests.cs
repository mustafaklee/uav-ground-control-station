using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using Gcs.Mavlink.Simulation;
using Gcs.Mavlink.Transports;
using Gcs.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Gcs.IntegrationTests.Commands;

/// <summary>
/// Vehicle control end to end: HTTP → lease check → audit row in PostgreSQL → COMMAND_LONG over real UDP →
/// simulated PX4 → COMMAND_ACK → audit outcome. The test owns the simulated vehicle, so it can make it misbehave.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommandEndpointTests(GcsApiFactory factory)
{
    private const string Vehicles = "/api/v1/vehicles";
    private const string Pilot = "pilot-1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_operator_flies_arm_takeoff_return_and_every_command_is_audited()
    {
        await using var vehicle = await ConnectSimulatorAsync(airborne: false);
        (await AcquireAsync(vehicle.Id, Pilot)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var arm = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Arm", Confirm: true));
        var takeoff = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Takeoff", Altitude: 20, Confirm: true));
        await WaitForTelemetryAsync(vehicle.Id, t => t.Position?.RelativeAltitude >= 19.5, "the vehicle climbed to 20 m");
        var disarmInFlight = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Disarm", Confirm: true));
        var rtl = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("ReturnToLaunch"));
        await WaitForTelemetryAsync(vehicle.Id, t => t.Flight?.Armed == false, "the vehicle landed and disarmed itself");

        arm.StatusCode.ShouldBe(HttpStatusCode.OK, await arm.Content.ReadAsStringAsync(Ct));
        var armed = (await arm.Content.ReadFromJsonAsync<CommandAuditResponse>(Ct))!;
        armed.Outcome.ShouldBe("Accepted");
        armed.Operator.ShouldBe(Pilot);
        armed.Attempts.ShouldBe(1);
        takeoff.StatusCode.ShouldBe(HttpStatusCode.OK);
        disarmInFlight.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(disarmInFlight)).ShouldBe("command.rejected");
        rtl.StatusCode.ShouldBe(HttpStatusCode.OK);

        var audit = await ListAuditAsync(vehicle.Id);
        audit.Items.Select(e => (e.Command, e.Outcome)).ShouldBe(
        [
            ("ReturnToLaunch", "Accepted"),
            ("Disarm", "Rejected"),
            ("Takeoff", "Accepted"),
            ("Arm", "Accepted"),
        ]);
        audit.Items[1].Detail.ShouldBe("The vehicle answered Denied.");
        audit.Items[2].Parameters.ShouldBe("altitude=20");
        audit.Items.ShouldAllBe(e => e.Callsign == vehicle.Callsign && e.Source == "GCS-API" && e.CompletedAt != null);
    }

    [Fact]
    public async Task A_command_the_vehicle_never_acknowledges_is_retried_then_times_out_with_504()
    {
        await using var vehicle = await ConnectSimulatorAsync();
        await AcquireAsync(vehicle.Id, Pilot);
        vehicle.Simulator.IgnoreCommands = true;

        var land = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land"));

        land.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await ReadCodeAsync(land)).ShouldBe("command.timed_out");
        vehicle.Simulator.CommandsReceived.Select(c => c.Confirmation).ShouldBe([(byte)0, (byte)1, (byte)2]); // 1 + 2 retries
        var entry = (await ListAuditAsync(vehicle.Id)).Items.ShouldHaveSingleItem();
        entry.Outcome.ShouldBe("TimedOut");
        entry.Attempts.ShouldBe(3);
        entry.Detail.ShouldBe("No COMMAND_ACK after 3 attempts (0.3 s each).");

        // The link itself stays up: only the command failed.
        (await _client.GetFromJsonAsync<VehicleLinkResponse>($"{Vehicles}/{vehicle.Id}/connection", Ct))!.State.ShouldBe("Connected");
    }

    [Fact]
    public async Task The_same_command_sent_twice_is_sent_once_and_the_second_request_is_refused()
    {
        await using var vehicle = await ConnectSimulatorAsync();
        await AcquireAsync(vehicle.Id, Pilot);
        vehicle.Simulator.IgnoreCommands = true; // keep the first LAND in flight long enough for the second to arrive

        var responses = await Task.WhenAll(
            SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land")),
            SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land")));

        var codes = await Task.WhenAll(responses.Select(ReadCodeAsync));
        codes.ShouldBe(["command.timed_out", "command.in_flight"], ignoreOrder: true);
        responses.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.GatewayTimeout, HttpStatusCode.Conflict], ignoreOrder: true);
        vehicle.Simulator.CommandsReceived.ShouldAllBe(c => c.Confirmation <= 2);
        vehicle.Simulator.CommandsReceived.Count.ShouldBe(3); // one command with its two retries, not two commands
        (await ListAuditAsync(vehicle.Id)).Items.Select(e => e.Outcome).ShouldBe(["TimedOut", "Refused"], ignoreOrder: true);
    }

    [Fact]
    public async Task Only_one_operator_controls_a_vehicle_at_a_time()
    {
        var vehicle = await RegisterAsync();

        var ali = await AcquireAsync(vehicle.Id, "ali");
        var ayseTakes = await AcquireAsync(vehicle.Id, "ayse");
        var ayseCommands = await SendAsync(vehicle.Id, "ayse", new SendCommandRequest("ReturnToLaunch"));
        var ayseReleases = await _client.SendAsync(Request(HttpMethod.Delete, $"{Vehicles}/{vehicle.Id}/command-lease", "ayse"), Ct);

        ali.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ali.Content.ReadFromJsonAsync<CommandLeaseResponse>(Ct))!.Holder.ShouldBe("ali");
        ayseTakes.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(ayseTakes)).ShouldBe("command.lease_held");
        (await ReadCodeAsync(ayseCommands)).ShouldBe("command.lease_held");
        (await ReadCodeAsync(ayseReleases)).ShouldBe("command.lease_held");

        (await _client.SendAsync(Request(HttpMethod.Delete, $"{Vehicles}/{vehicle.Id}/command-lease", "ali"), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await AcquireAsync(vehicle.Id, "ayse")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetFromJsonAsync<CommandLeaseResponse>($"{Vehicles}/{vehicle.Id}/command-lease", Ct))!.Holder.ShouldBe("ayse");

        // The refused attempt is on file too, with who tried it.
        var refused = (await ListAuditAsync(vehicle.Id)).Items.ShouldHaveSingleItem();
        (refused.Operator, refused.Outcome).ShouldBe(("ayse", "Refused"));
    }

    [Fact]
    public async Task Commands_without_the_lease_or_without_a_link_are_refused_and_audited()
    {
        var vehicle = await RegisterAsync();

        var noLease = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land"));
        await AcquireAsync(vehicle.Id, Pilot);
        var noLink = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land"));

        noLease.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(noLease)).ShouldBe("command.lease_required");
        (await ReadCodeAsync(noLink)).ShouldBe("command.not_connected");
        (await ListAuditAsync(vehicle.Id)).Items.ShouldAllBe(e => e.Outcome == "Refused" && e.Attempts == 0);
    }

    [Fact]
    public async Task Requests_are_validated_before_anything_is_sent_or_recorded()
    {
        var vehicle = await RegisterAsync();
        await AcquireAsync(vehicle.Id, Pilot);

        var noOperator = await _client.PostAsJsonAsync($"{Vehicles}/{vehicle.Id}/commands", new SendCommandRequest("Land"), Ct);
        var unconfirmed = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Arm"));
        var badAltitude = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Takeoff", Altitude: 0, Confirm: true));
        var badMode = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("SetMode", Mode: "WARP", Confirm: true));
        var unknown = await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Fly"));

        (await ReadCodeAsync(noOperator)).ShouldBe("command.operator_required");
        (await ReadCodeAsync(unconfirmed)).ShouldBe("command.confirmation_required");
        (await ReadCodeAsync(badAltitude)).ShouldBe("command.takeoff.altitude");
        (await ReadCodeAsync(badMode)).ShouldBe("command.mode.unknown");
        (await ReadCodeAsync(unknown)).ShouldBe("command.unknown");
        new[] { noOperator, unconfirmed, badAltitude, badMode, unknown }.ShouldAllBe(r => r.StatusCode == HttpStatusCode.BadRequest);
        (await ListAuditAsync(vehicle.Id)).Items.ShouldBeEmpty();

        var modes = await _client.GetFromJsonAsync<FlightModesResponse>($"{Vehicles}/{vehicle.Id}/flight-modes", Ct);
        modes!.Modes.ShouldContain("AUTO.LOITER");
        modes.Modes.ShouldContain("POSCTL");
    }

    [Fact]
    public async Task The_database_refuses_to_delete_or_rewrite_audit_rows()
    {
        var vehicle = await RegisterAsync();
        await SendAsync(vehicle.Id, Pilot, new SendCommandRequest("Land")); // refused (no lease): a completed row

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GcsDbContext>();
        var delete = await Should.ThrowAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM gcs.command_audit WHERE vehicle_id = {vehicle.Id}", Ct));
        var rewrite = await Should.ThrowAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE gcs.command_audit SET outcome = 'Accepted' WHERE vehicle_id = {vehicle.Id}", Ct));

        delete.MessageText.ShouldContain("append-only");
        rewrite.MessageText.ShouldContain("cannot be changed");
        (await ListAuditAsync(vehicle.Id)).Items.ShouldHaveSingleItem().Outcome.ShouldBe("Refused");
    }

    private async Task<VehicleResponse> RegisterAsync(int? port = null)
    {
        var request = TestData.Registration() with { Connection = new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: port ?? FreeUdpPort()) };
        var response = await _client.PostAsJsonAsync(Vehicles, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    /// <summary>Registers a vehicle, starts a simulated PX4 on the other end of a UDP socket and waits until it is connected.</summary>
    private async Task<ConnectedVehicle> ConnectSimulatorAsync(bool airborne = true)
    {
        var port = FreeUdpPort();
        var registered = await RegisterAsync(port);
        var simulator = new SimulatedVehicle(new SimulatedVehicleOptions { SystemId = (byte)registered.MavlinkSystemId, StartAirborne = airborne });
        var vehicle = new ConnectedVehicle(registered, simulator, port, _client);

        await _client.PostAsync(new Uri($"{Vehicles}/{registered.Id}/connection", UriKind.Relative), null, Ct);
        await Eventually.GetAsync(
            async () => (await _client.GetFromJsonAsync<VehicleLinkResponse>($"{Vehicles}/{registered.Id}/connection", Ct))!,
            link => link.State == "Connected", Timeout, "the link is connected");
        await WaitForTelemetryAsync(registered.Id, t => t.Position is not null, "position telemetry arrived");
        return vehicle;
    }

    private Task<HttpResponseMessage> AcquireAsync(Guid vehicleId, string operatorName) =>
        _client.SendAsync(Request(HttpMethod.Post, $"{Vehicles}/{vehicleId}/command-lease", operatorName), Ct);

    private Task<HttpResponseMessage> SendAsync(Guid vehicleId, string operatorName, SendCommandRequest command)
    {
        var request = Request(HttpMethod.Post, $"{Vehicles}/{vehicleId}/commands", operatorName);
        request.Content = JsonContent.Create(command);
        return _client.SendAsync(request, Ct);
    }

    private async Task<PagedResponse<CommandAuditResponse>> ListAuditAsync(Guid vehicleId) =>
        (await _client.GetFromJsonAsync<PagedResponse<CommandAuditResponse>>($"{Vehicles}/{vehicleId}/commands", Ct))!;

    private Task<TelemetryResponse> WaitForTelemetryAsync(Guid vehicleId, Func<TelemetryResponse, bool> condition, string because) =>
        Eventually.GetAsync(
            async () =>
            {
                var response = await _client.GetAsync(new Uri($"{Vehicles}/{vehicleId}/telemetry", UriKind.Relative), Ct);
                return response.IsSuccessStatusCode
                    ? (await response.Content.ReadFromJsonAsync<TelemetryResponse>(Ct))!
                    : new TelemetryResponse(vehicleId, DateTimeOffset.MinValue, null, null, null, null, null, null);
            },
            condition, Timeout, because);

    private static HttpRequestMessage Request(HttpMethod method, string path, string operatorName)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(CommandHeaders.Operator, operatorName);
        return request;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
        return problem.RootElement.GetProperty("code").GetString();
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>A registered vehicle with a running simulator; disposing disconnects and stops it.</summary>
    private sealed class ConnectedVehicle : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;

        public ConnectedVehicle(VehicleResponse vehicle, SimulatedVehicle simulator, int port, HttpClient client)
        {
            Id = vehicle.Id;
            Callsign = vehicle.Callsign;
            Simulator = simulator;
            _client = client;
            var runner = new SimulatedVehicleRunner(simulator, UdpMavlinkTransport.Connect(new IPEndPoint(IPAddress.Loopback, port)), TimeProvider.System);
            _running = Task.Run(() => runner.RunAsync(_stop.Token), CancellationToken.None);
        }

        public Guid Id { get; }

        public string Callsign { get; }

        public SimulatedVehicle Simulator { get; }

        public async ValueTask DisposeAsync()
        {
            await _client.DeleteAsync(new Uri($"{Vehicles}/{Id}/connection", UriKind.Relative), CancellationToken.None);
            await _stop.CancelAsync();
            await _running;
            _stop.Dispose();
        }
    }
}
