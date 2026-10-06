using Gcs.Domain.Vehicles;
using Gcs.Domain.Vehicles.Connections;

namespace Gcs.UnitTests.Domain.Vehicles;

public sealed class VehicleConnectionTests
{
    private const int MaxAttempts = 3;
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connecting, true)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Reconnecting, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Reconnecting, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Faulted, false)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Reconnecting, ConnectionState.Faulted, true)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Connecting, true)]
    public void Transition_table_allows_only_meaningful_moves(ConnectionState from, ConnectionState to, bool allowed)
    {
        ConnectionStateMachine.CanTransition(from, to).ShouldBe(allowed);
    }

    [Fact]
    public void Happy_path_connect_then_heartbeat_reaches_connected()
    {
        var connection = NewConnection();

        connection.BeginConnect().IsSuccess.ShouldBeTrue();
        connection.HeartbeatReceived(T0).IsSuccess.ShouldBeTrue();

        connection.State.ShouldBe(ConnectionState.Connected);
        connection.LastHeartbeatAt.ShouldBe(T0);
    }

    [Fact]
    public void Heartbeat_without_connect_request_is_rejected()
    {
        var connection = NewConnection();

        connection.HeartbeatReceived(T0).IsSuccess.ShouldBeFalse();
        connection.State.ShouldBe(ConnectionState.Disconnected);
    }

    [Fact]
    public void Lost_heartbeat_then_recovery_returns_to_connected_and_resets_attempts()
    {
        var connection = ConnectedConnection();

        connection.HeartbeatLost();
        connection.ReconnectAttemptFailed();
        connection.HeartbeatReceived(T0.AddSeconds(10));

        connection.State.ShouldBe(ConnectionState.Connected);
        connection.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public void Reconnect_attempts_are_bounded_and_end_in_faulted()
    {
        var connection = ConnectedConnection();
        connection.HeartbeatLost();

        for (var attempt = 1; attempt < MaxAttempts; attempt++)
        {
            connection.ReconnectAttemptFailed();
            connection.State.ShouldBe(ConnectionState.Reconnecting);
        }

        connection.ReconnectAttemptFailed();

        connection.State.ShouldBe(ConnectionState.Faulted);
        connection.FaultReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Operator_can_retry_a_faulted_link()
    {
        var connection = NewConnection();
        connection.BeginConnect();
        connection.Fault("No heartbeat within 5 s.");

        connection.BeginConnect().IsSuccess.ShouldBeTrue();

        connection.State.ShouldBe(ConnectionState.Connecting);
        connection.FaultReason.ShouldBeNull();
    }

    [Fact]
    public void Disconnect_is_allowed_from_any_state_and_is_idempotent()
    {
        var connection = ConnectedConnection();

        connection.Disconnect().IsSuccess.ShouldBeTrue();
        connection.Disconnect().IsSuccess.ShouldBeTrue();

        connection.State.ShouldBe(ConnectionState.Disconnected);
    }

    private static VehicleConnection NewConnection() => new(VehicleId.New(), MaxAttempts);

    private static VehicleConnection ConnectedConnection()
    {
        var connection = NewConnection();
        connection.BeginConnect();
        connection.HeartbeatReceived(T0);
        return connection;
    }
}
