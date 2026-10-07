# ADR-011: Redis evaluated in Phase 4 and not adopted yet; triggers for adopting it

* Status: Accepted
* Date: 2026-10-07
* Follows: [ADR-008](ADR-008-redis-deferred.md)

## Context

ADR-008 postponed Redis to Phase 4, when live telemetry would exist and the need could be measured. Phase 4 built the
telemetry path: decoded messages update a latest-state snapshot per vehicle, a broadcaster pushes snapshots over
SignalR at 5 Hz, and history is sampled at 1 Hz into PostgreSQL. The candidate Redis uses were:

| Candidate use | What it would solve |
|---|---|
| Latest-state cache | Share snapshots between API instances; survive an API restart |
| SignalR backplane | Deliver pushes to clients connected to *other* API instances |
| Pub/sub between link workers and API | Run MAVLink links in a separate process from the HTTP API |

## Measurements and reasoning

* The system runs **one API instance**. A `ConcurrentDictionary` lookup costs nanoseconds; a Redis round trip costs
  hundreds of microseconds plus serialization, at up to 50 updates per second per vehicle.
* **Restart survival has no value for live state.** After a restart the links reconnect and the first heartbeat and
  telemetry burst rebuild the snapshot within about a second. Stale pre-restart data would be misleading, not helpful.
* **Durable history already has a home:** sampled into PostgreSQL in batches.
* **Another network dependency is a new failure mode on the flight-critical path.** With Redis on the hot path, a Redis
  outage would blank every operator screen. In memory, that failure mode does not exist.

## Decision

Do **not** use Redis yet. The latest-state store stays in memory behind `ITelemetryService` / `ITelemetrySink`, so
switching is an adapter change. The `redis` container stays in docker-compose so the environment is ready.

Adopt Redis when **any** of these becomes true:

1. **More than one API instance** (high availability or load). Then: Redis as the SignalR backplane
   (`AddStackExchangeRedis`) and as a shared latest-state store.
2. **Links run in separate worker processes** (e.g. one per radio or ground site). Then: Redis Streams or pub/sub from
   workers to the API.
3. **A measured requirement** for state shared across services that PostgreSQL cannot serve fast enough.

When adopted, compare Redis 8 (AGPLv3 / RSALv2 / SSPLv1) with Valkey (BSD) for licensing.

## Consequences

* No Redis code or dependency in Phase 4; one less thing to secure and monitor.
* The single-instance assumption is now explicit and documented here, with the conditions that end it.
