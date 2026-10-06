# ADR-007: MAVLink abstraction strategy and library choice

* Status: Accepted
* Date: 2026-10-06

## Context

The GCS must talk to PX4, ArduPilot, PX4 SITL and our own simulator over UDP, TCP or serial links, and later over
radios or modems. MAVLink framing, CRC, signing and dialects are intricate and mistakes are safety-relevant. The UI
and the API must never handle sockets, serial ports or raw packets.

## Decision

* Define ports in the Application layer and implement them in `Gcs.Mavlink`:
  * `IMavlinkTransport`: UDP, TCP, Serial and Simulator implementations.
  * `IMavlinkConnection`: one link per vehicle with heartbeat, timeout and a
    Connected / Connecting / Reconnecting / Disconnected / Faulted state machine with bounded exponential backoff.
  * `IMavlinkMessageParser`, `IMavlinkCommandService` (COMMAND_LONG with ack, retry and timeout) and `ITelemetryService`.
* Autopilot differences (PX4 vs. ArduPilot flight modes, mission protocol quirks) live behind an autopilot profile
  abstraction, not in callers.
* Use the **Asv.Mavlink** library (MIT licence, actively maintained, native .NET, MAVLink v2 with generated dialects)
  for message definitions, framing and parsing, wrapped behind our own interfaces so it can be replaced.

## Alternatives considered

* **Hand-written parser generated from the XML definitions**: maximum control and good learning value, large effort and risk.
* **MAVSDK** (C++ core with a gRPC server): high level and well tested, but adds a sidecar process and hides the protocol details we want to own.
* **Older .NET MAVLink ports (e.g. from Mission Planner)**: GPL licensed or less maintained.

## Consequences

* The simulator and real vehicles use the same code path above the transport.
* The wrapper must be covered by tests with recorded and simulated messages (Phase 3).
* Upgrading or replacing Asv.Mavlink is contained to `Gcs.Mavlink`.
