using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Gcs.Contracts.Realtime;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using RabbitMQ.Client;

namespace Gcs.IntegrationTests.Vehicles;

/// <summary>
/// Phase 4 end to end: live telemetry over SignalR (throttled), link status push, sampled history in PostgreSQL and
/// link events delivered to RabbitMQ through the outbox. Each test uses the in-process simulator transport.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RealtimeAndHistoryTests(GcsApiFactory factory)
{
    private const string BasePath = "/api/v1/vehicles";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscribed_client_receives_live_telemetry_at_the_throttled_rate()
    {
        var vehicle = await RegisterSimulatorAsync();
        var updates = new ConcurrentQueue<TelemetryResponse>();
        await using var hub = await StartHubAsync(RealtimeRoutes.TelemetryHub);
        hub.On<TelemetryResponse>(nameof(ITelemetryHubClient.TelemetryUpdated), updates.Enqueue);
        await hub.InvokeAsync(TelemetryHubMethods.SubscribeVehicle, vehicle.Id, Ct);

        await ConnectAsync(vehicle.Id);
        await WaitUntilAsync(() => updates.Any(u => u.Position is not null && u.Flight is not null), "telemetry arrives over SignalR");

        // The simulator sends ~50 messages per second; the broadcaster pushes at most 5 snapshots per second.
        var countBefore = updates.Count;
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var pushedInTwoSeconds = updates.Count - countBefore;
        pushedInTwoSeconds.ShouldBeInRange(4, 13);
        updates.Last().VehicleId.ShouldBe(vehicle.Id);

        await DisconnectAsync(vehicle.Id);
    }

    [Fact]
    public async Task Clients_receive_link_state_changes_for_the_fleet()
    {
        var vehicle = await RegisterSimulatorAsync();
        var statuses = new ConcurrentQueue<VehicleLinkResponse>();
        await using var hub = await StartHubAsync(RealtimeRoutes.VehiclesHub);
        hub.On<VehicleLinkResponse>(nameof(IVehiclesHubClient.LinkStatusChanged), statuses.Enqueue);

        await ConnectAsync(vehicle.Id);
        await WaitUntilAsync(() => statuses.Any(s => s.VehicleId == vehicle.Id && s.State == "Connected"), "Connected is pushed");
        await DisconnectAsync(vehicle.Id);
        await WaitUntilAsync(() => statuses.Any(s => s.VehicleId == vehicle.Id && s.State == "Disconnected"), "Disconnected is pushed");

        statuses.Where(s => s.VehicleId == vehicle.Id).Select(s => s.State)
            .ShouldBe(["Connecting", "Connected", "Disconnected"]);
    }

    [Fact]
    public async Task Telemetry_history_is_sampled_and_stored_in_postgres()
    {
        var vehicle = await RegisterSimulatorAsync();
        await ConnectAsync(vehicle.Id);

        var history = await Eventually.GetAsync(
            () => _client.GetFromJsonAsync<TelemetryHistoryResponse>($"{BasePath}/{vehicle.Id}/telemetry/history", Ct)!,
            h => h!.Samples.Count >= 3,
            Timeout,
            "at least three samples are stored");
        await DisconnectAsync(vehicle.Id);

        history!.Truncated.ShouldBeFalse();
        history.Samples.Select(s => s.UpdatedAt).ShouldBeInOrder();
        var sample = history.Samples[^1];
        sample.Position.ShouldNotBeNull();
        sample.Flight.ShouldBe(new FlightDto(Armed: true, "AUTO.LOITER"));
        sample.Gps!.Fix.ShouldBe("Fix3D");
    }

    [Fact]
    public async Task History_rejects_an_inverted_time_window()
    {
        var vehicle = await RegisterSimulatorAsync();
        var now = DateTimeOffset.UtcNow;

        var response = await _client.GetAsync(
            new Uri($"{BasePath}/{vehicle.Id}/telemetry/history?from={Uri.EscapeDataString(now.ToString("O"))}&to={Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"))}", UriKind.Relative),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Vehicle_and_link_events_reach_rabbitmq_in_order_through_the_outbox()
    {
        await using var connection = await new ConnectionFactory { Uri = factory.RabbitMqUri }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await channel.ExchangeDeclareAsync("gcs.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: Ct);
        var queue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue.QueueName, "gcs.events", "vehicle.*", cancellationToken: Ct);

        var vehicle = await RegisterSimulatorAsync();
        await ConnectAsync(vehicle.Id);
        await Eventually.GetAsync(
            () => _client.GetFromJsonAsync<VehicleLinkResponse>($"{BasePath}/{vehicle.Id}/connection", Ct)!,
            l => l!.State == "Connected", Timeout, "connected");
        await DisconnectAsync(vehicle.Id);

        var types = new List<string>();
        var deadline = DateTime.UtcNow + Timeout;
        while (!types.Contains("VehicleDisconnected") && DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue.QueueName, autoAck: true, Ct);
            if (message is null)
            {
                await Task.Delay(100, Ct);
                continue;
            }

            if (Encoding.UTF8.GetString(message.Body.Span).Contains(vehicle.Id.ToString(), StringComparison.Ordinal))
            {
                types.Add(message.BasicProperties.Type!);
            }
        }

        // Aggregate events (registration) and link events share one outbox, so consumers see them in the order they happened.
        types.ShouldBe(["VehicleRegistered", "VehicleConnected", "VehicleDisconnected"]);
    }

    private async Task<HubConnection> StartHubAsync(string route)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, route), options =>
            {
                // The in-memory test server has no real sockets, so use HTTP long polling through its handler.
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private async Task<VehicleResponse> RegisterSimulatorAsync()
    {
        var request = TestData.Registration() with { Connection = new ConnectionSettingsDto("Simulator") };
        var response = await _client.PostAsJsonAsync(BasePath, request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
    }

    private async Task ConnectAsync(Guid id) =>
        (await _client.PostAsync(new Uri($"{BasePath}/{id}/connection", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

    private async Task DisconnectAsync(Guid id) =>
        await _client.DeleteAsync(new Uri($"{BasePath}/{id}/connection", UriKind.Relative), Ct);

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(50, Ct);
        }
    }
}
