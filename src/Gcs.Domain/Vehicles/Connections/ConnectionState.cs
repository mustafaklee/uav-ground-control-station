namespace Gcs.Domain.Vehicles.Connections;

public enum ConnectionState
{
    /// <summary>No link and none requested.</summary>
    Disconnected = 1,

    /// <summary>Operator asked to connect; waiting for the first heartbeat.</summary>
    Connecting = 2,

    /// <summary>Heartbeats are arriving within the timeout.</summary>
    Connected = 3,

    /// <summary>Heartbeats stopped; retrying with backoff while the operator still wants the link.</summary>
    Reconnecting = 4,

    /// <summary>Connecting or reconnecting gave up. Needs an operator decision to try again.</summary>
    Faulted = 5,
}
