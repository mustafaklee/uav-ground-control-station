# ADR-006: SignalR for real-time push to clients

* Status: Accepted
* Date: 2026-10-06

## Context

Operators must see live telemetry, vehicle state, alarms and mission progress for multiple vehicles with low latency.
Polling REST endpoints would waste bandwidth and add delay.

## Decision

Use **ASP.NET Core SignalR** (WebSockets with automatic fallback) for server-to-client push, with hubs per concern
(`/hubs/telemetry`, `/hubs/vehicles`, `/hubs/alarms`, `/hubs/missions`) and one group per vehicle, so a client only
receives what it subscribed to. Telemetry pushed to clients is throttled (for example 5 to 10 Hz per vehicle) from
the latest-state store. Commands and CRUD stay on REST, where they get normal HTTP semantics, validation,
authorization and audit.

## Alternatives considered

* **Raw WebSockets**: full control, but reconnection, grouping and serialization would be re-implemented.
* **gRPC streaming**: efficient and strongly typed; weaker browser support and less natural for group fan-out.
* **MQTT to clients**: good for IoT fan-out, but needs another broker and auth model on the client side.

## Consequences

* A .NET client library is available for Avalonia; the same JWT authentication as the REST API.
* Running several API instances later needs a backplane (Redis, see ADR-008); a single instance does not.
