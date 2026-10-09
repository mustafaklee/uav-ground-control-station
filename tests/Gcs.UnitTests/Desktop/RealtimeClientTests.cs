using Gcs.Desktop.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gcs.UnitTests.Desktop;

public sealed class RealtimeClientTests
{
    [Theory]
    [InlineData(HubConnectionState.Connected, HubConnectionState.Connected, BackendConnectionState.Connected)]
    [InlineData(HubConnectionState.Connected, HubConnectionState.Reconnecting, BackendConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Reconnecting, HubConnectionState.Connected, BackendConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Connected, HubConnectionState.Connecting, BackendConnectionState.Connecting)]
    [InlineData(HubConnectionState.Connected, HubConnectionState.Disconnected, BackendConnectionState.Disconnected)]
    [InlineData(HubConnectionState.Disconnected, HubConnectionState.Disconnected, BackendConnectionState.Disconnected)]
    public void The_backend_counts_as_connected_only_while_both_hubs_are(
        HubConnectionState telemetry, HubConnectionState vehicles, BackendConnectionState expected)
    {
        // Link status and quality travel on the vehicles hub: telemetry alone must not look like a healthy backend.
        RealtimeClient.Combine(telemetry, vehicles).ShouldBe(expected);
    }
}
