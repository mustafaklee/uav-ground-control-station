using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Health;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace Gcs.IntegrationTests.Observability;

/// <summary>
/// "Can we follow one operator action through the system?" The test starts the trace itself (W3C traceparent header,
/// exactly like an instrumented client would), then checks that every hop carried it on.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class ObservabilityTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly GcsApiFactory factory;
    private readonly HttpClient _admin;
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public ObservabilityTests(GcsApiFactory factory)
    {
        this.factory = factory;
        _admin = factory.CreateAdminClient();
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Gcs" or "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task One_command_is_one_trace_http_request_database_and_mavlink_exchange()
    {
        var vehicle = await ConnectSimulatorAsync();
        (await _admin.PostAsync(new Uri($"/api/v1/vehicles/{vehicle}/command-lease", UriKind.Relative), null, Ct)).EnsureSuccessStatusCode();
        var traceId = ActivityTraceId.CreateRandom();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vehicles/{vehicle}/commands")
        {
            Content = JsonContent.Create(new SendCommandRequest("Land")),
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await _admin.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var trace = await WaitForTraceAsync(traceId, spans => spans.Any(s => s.OperationName == "vehicle.command Land") && spans.Any(IsHttpServer));

        var server = trace.Single(IsHttpServer);
        var command = trace.Single(s => s.OperationName == "vehicle.command Land");
        command.GetTagItem("gcs.command.outcome").ShouldBe("Accepted");
        command.GetTagItem("gcs.vehicle.id").ShouldBe(vehicle.ToString());
        command.Events.ShouldContain(e => e.Name == "COMMAND_LONG sent");
        trace.Count(s => s.Source.Name == "Npgsql").ShouldBeGreaterThanOrEqualTo(2); // audit INSERT before, UPDATE after
        server.GetTagItem("gcs.correlation_id").ShouldNotBeNull();
    }

    [Fact]
    public async Task An_event_published_seconds_later_continues_the_trace_of_the_request_that_raised_it()
    {
        await using var connection = await new ConnectionFactory { Uri = factory.RabbitMqUri }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await channel.ExchangeDeclareAsync("gcs.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: Ct);
        var queue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue.QueueName, "gcs.events", "vehicle.registered", cancellationToken: Ct);
        var traceId = ActivityTraceId.CreateRandom();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/vehicles") { Content = JsonContent.Create(TestData.Registration()) };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var vehicle = await (await _admin.SendAsync(request, Ct)).Content.ReadFromJsonAsync<VehicleResponse>(Ct);

        var deadline = DateTime.UtcNow + Timeout;
        string? traceParent = null;
        while (traceParent is null && DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue.QueueName, autoAck: true, Ct);
            if (message is null)
            {
                await Task.Delay(100, Ct);
            }
            else if (Encoding.UTF8.GetString(message.Body.Span).Contains(vehicle!.Id.ToString(), StringComparison.Ordinal))
            {
                traceParent = Encoding.UTF8.GetString((byte[])message.BasicProperties.Headers!["traceparent"]!);
            }
        }

        traceParent.ShouldNotBeNull();
        ActivityContext.Parse(traceParent, null).TraceId.ShouldBe(traceId);
        var trace = await WaitForTraceAsync(traceId, spans => spans.Any(s => s.OperationName == "VehicleRegistered publish"));
        trace.Single(s => s.OperationName == "VehicleRegistered publish").Kind.ShouldBe(ActivityKind.Producer);
    }

    [Fact]
    public async Task Health_details_name_the_faulted_vehicle_and_need_a_signed_in_user()
    {
        // A UDP vehicle nobody sends to: the link gives up after the connect timeout.
        var register = await _admin.PostAsJsonAsync(
            "/api/v1/vehicles", TestData.Registration() with { Connection = new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: FreeUdpPort()) }, Ct);
        var silent = (await register.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
        await _admin.PostAsync(new Uri($"/api/v1/vehicles/{silent.Id}/connection", UriKind.Relative), null, Ct);

        var report = await Eventually.GetAsync(
            async () => (await _admin.GetFromJsonAsync<HealthReportResponse>("/health/details", Ct))!,
            r => r.Checks.Single(c => c.Name == "vehicle-links").Data!.TryGetValue(silent.Id.ToString(), out var state)
                && ((JsonElement)state).GetString()!.StartsWith("Faulted", StringComparison.Ordinal),
            Timeout,
            "the silent vehicle is reported as faulted");
        using var anonymous = factory.CreateClient();

        report.Checks.Select(c => c.Name).ShouldBe(["postgres", "rabbitmq", "telemetry-history", "vehicle-links", "outbox-backlog"], ignoreOrder: true);
        var links = report.Checks.Single(c => c.Name == "vehicle-links");
        links.Status.ShouldBe("Degraded");
        ((JsonElement)links.Data![silent.Id.ToString()]).GetString().ShouldStartWith("Faulted");
        (await anonymous.GetAsync(new Uri("/health/details", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await _admin.DeleteAsync(new Uri($"/api/v1/vehicles/{silent.Id}/connection", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task Commands_are_counted_by_command_and_outcome()
    {
        var measurements = new ConcurrentQueue<(long Value, string? Command, string? Outcome)>();
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Gcs" && instrument.Name == "gcs.commands")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? command = null, outcome = null;
            foreach (var tag in tags)
            {
                command = tag.Key == "gcs.command" ? tag.Value?.ToString() : command;
                outcome = tag.Key == "gcs.command.outcome" ? tag.Value?.ToString() : outcome;
            }

            measurements.Enqueue((value, command, outcome));
        });
        meters.Start();

        var vehicle = await ConnectSimulatorAsync();
        await _admin.PostAsync(new Uri($"/api/v1/vehicles/{vehicle}/command-lease", UriKind.Relative), null, Ct);
        await _admin.PostAsJsonAsync($"/api/v1/vehicles/{vehicle}/commands", new SendCommandRequest("Disarm", Confirm: true), Ct); // denied in flight

        measurements.ShouldContain((1L, "Disarm", "Rejected"));
    }

    private async Task<Guid> ConnectSimulatorAsync()
    {
        var register = await _admin.PostAsJsonAsync("/api/v1/vehicles", TestData.Registration() with { Connection = new ConnectionSettingsDto("Simulator") }, Ct);
        var vehicle = (await register.Content.ReadFromJsonAsync<VehicleResponse>(Ct))!;
        await _admin.PostAsync(new Uri($"/api/v1/vehicles/{vehicle.Id}/connection", UriKind.Relative), null, Ct);
        await Eventually.GetAsync(
            async () => (await _admin.GetFromJsonAsync<VehicleLinkResponse>($"/api/v1/vehicles/{vehicle.Id}/connection", Ct))!,
            l => l.State == "Connected", Timeout, "the simulator is connected");
        return vehicle.Id;
    }

    private async Task<List<Activity>> WaitForTraceAsync(ActivityTraceId traceId, Func<List<Activity>, bool> complete)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var spans = _activities.Where(a => a.TraceId == traceId).ToList();
            if (complete(spans) || DateTime.UtcNow > deadline)
            {
                return spans;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static int FreeUdpPort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static bool IsHttpServer(Activity activity) =>
        activity.Source.Name == "Microsoft.AspNetCore" && activity.Kind == ActivityKind.Server;
}
