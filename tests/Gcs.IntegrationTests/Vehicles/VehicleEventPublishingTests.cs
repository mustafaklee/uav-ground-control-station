using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gcs.Contracts.Vehicles;
using Gcs.IntegrationTests.Infrastructure;
using RabbitMQ.Client;

namespace Gcs.IntegrationTests.Vehicles;

/// <summary>
/// The outbox end to end: registering a vehicle stores a VehicleRegistered event in PostgreSQL in the same
/// transaction, and the background dispatcher delivers it to the RabbitMQ events exchange.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class VehicleEventPublishingTests(GcsApiFactory factory)
{
    private const string EventsExchange = "gcs.events";
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Registering_a_vehicle_publishes_vehicle_registered_to_rabbitmq()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await new ConnectionFactory { Uri = factory.RabbitMqUri }.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        var queue = await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);
        await channel.QueueBindAsync(queue.QueueName, EventsExchange, routingKey: "vehicle.*", cancellationToken: ct);

        using var client = factory.CreateAdminClient();
        var callsign = TestData.NextCallsign("EVT");
        var response = await client.PostAsJsonAsync("/api/v1/vehicles", TestData.Registration(callsign), ct);
        var vehicle = await response.Content.ReadFromJsonAsync<VehicleResponse>(ct);

        var message = await WaitForMessageAsync(channel, queue.QueueName, m => m.BasicProperties.Type == "VehicleRegistered"
            && Encoding.UTF8.GetString(m.Body.Span).Contains(vehicle!.Id.ToString(), StringComparison.Ordinal), ct);

        message.RoutingKey.ShouldBe("vehicle.registered");
        message.BasicProperties.ContentType.ShouldBe("application/json");
        using var payload = JsonDocument.Parse(message.Body);
        payload.RootElement.GetProperty("vehicleId").GetGuid().ShouldBe(vehicle!.Id);
        payload.RootElement.GetProperty("callsign").GetString().ShouldBe(callsign);
        payload.RootElement.GetProperty("autopilot").GetString().ShouldBe("Px4");
    }

    private static async Task<BasicGetResult> WaitForMessageAsync(
        IChannel channel, string queue, Func<BasicGetResult, bool> match, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
            if (message is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            if (match(message))
            {
                return message;
            }
        }

        throw new TimeoutException($"No matching message arrived on '{queue}' within {DeliveryTimeout.TotalSeconds} s.");
    }
}
