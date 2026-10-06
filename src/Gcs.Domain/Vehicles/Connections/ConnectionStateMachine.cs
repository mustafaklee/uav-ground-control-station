namespace Gcs.Domain.Vehicles.Connections;

/// <summary>
/// The allowed link state transitions, as one explicit table. Anything not listed is a bug in the caller
/// (for example "Disconnected → Connected" without ever connecting) and is rejected.
/// </summary>
///
/// <remarks>
/// <code>
///   Disconnected ──connect──► Connecting ──heartbeat──► Connected
///        ▲                      │   │                    │
///        │                 give up  cancel        heartbeat lost
///        │                      ▼   │                    ▼
///        └───disconnect──── Faulted ◄──retries exhausted── Reconnecting ──heartbeat──► Connected
/// </code>
/// Every state except Disconnected can go to Disconnected when the operator ends the link.
/// </remarks>
public static class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> AllowedTransitions = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] = [ConnectionState.Connected, ConnectionState.Faulted, ConnectionState.Disconnected],
        [ConnectionState.Connected] = [ConnectionState.Reconnecting, ConnectionState.Disconnected],
        [ConnectionState.Reconnecting] = [ConnectionState.Connected, ConnectionState.Faulted, ConnectionState.Disconnected],
        [ConnectionState.Faulted] = [ConnectionState.Connecting, ConnectionState.Disconnected],
    };

    public static bool CanTransition(ConnectionState from, ConnectionState to) =>
        AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);
}
