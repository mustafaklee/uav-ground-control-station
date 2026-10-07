using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Gcs.Infrastructure.LinkEvents;

namespace Gcs.UnitTests.Infrastructure;

public sealed class LinkEventMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly VehicleId Vehicle = VehicleId.New();

    [Theory]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected, typeof(VehicleConnected))]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Connected, typeof(VehicleConnected))]
    [InlineData(ConnectionState.Connected, ConnectionState.Reconnecting, typeof(VehicleLinkLost))]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Faulted, typeof(VehicleLinkFaulted))]
    [InlineData(ConnectionState.Connecting, ConnectionState.Faulted, typeof(VehicleLinkFaulted))]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected, typeof(VehicleDisconnected))]
    public void Meaningful_transitions_become_integration_events(ConnectionState from, ConnectionState to, Type expected)
    {
        var linkEvent = VehicleLinkEventDispatcher.ToEvent(Status(to), from, Now);

        linkEvent.ShouldNotBeNull().ShouldBeOfType(expected);
        linkEvent.OccurredAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Connecting)]
    public void Routine_transitions_publish_no_event(ConnectionState from, ConnectionState to)
    {
        VehicleLinkEventDispatcher.ToEvent(Status(to), from, Now).ShouldBeNull();
    }

    private static VehicleLinkStatus Status(ConnectionState state) =>
        new(Vehicle, state, null, 0, state == ConnectionState.Faulted ? "No heartbeat." : null, new LinkQuality(0, 0, 0, 0));
}
