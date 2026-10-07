# Architecture

## Goals and priorities

The GCS is built as if it were a defence product. When two goals conflict, the order is:
**reliability → safety → security → observability → maintainability → performance → scalability → developer experience.**

## System context

```
            ┌──────────────────────┐
            │  Avalonia GCS client │  operator workstation (Windows / Linux)
            └──────────┬───────────┘
          REST (HTTPS) │ SignalR (WebSocket)
            ┌──────────▼───────────┐
            │     Gcs.Api host     │  ASP.NET Core, modular monolith
            └──┬───────┬────────┬──┘
               │       │        │ MAVLink (UDP / TCP / serial)
       ┌───────▼──┐ ┌──▼─────┐ ┌▼───────────────────────────┐
       │PostgreSQL│ │RabbitMQ│ │ Vehicles: PX4 / ArduPilot, │
       └──────────┘ └────────┘ │ SITL, Gcs.Simulation       │
                               └────────────────────────────┘
```

* **REST** handles CRUD, configuration, mission management and user management.
* **SignalR** pushes live telemetry, vehicle state, alarms and events to clients.
* **MAVLink** is only for talking to vehicles. Nothing above `Gcs.Mavlink` sees packets, sockets or serial ports.
* **RabbitMQ** carries *domain events* (vehicle connected, mission uploaded, alarm raised) for asynchronous
  consumers such as the audit log, notifications and future services. High-rate telemetry does **not** go through
  the broker (see [ADR-005](adr/ADR-005-rabbitmq.md)).

## Style: modular monolith + Clean Architecture

A single deployable backend, split into projects whose dependencies point inwards only
([ADR-003](adr/ADR-003-modular-monolith.md)). Architecture tests (`tests/Gcs.ArchitectureTests`) fail the build if a
layer takes a dependency it should not.

```
                 ┌───────────────┐
                 │  Gcs.Domain   │  entities, value objects, domain events. No framework references.
                 └───────▲───────┘
                 ┌───────┴───────┐
                 │Gcs.Application│  use cases, ports (interfaces), validation. Knows Domain + Contracts.
                 └───────▲───────┘
   ┌──────────┬──────────┼───────────┬──────────────┐
┌──┴───────┐┌─┴────────┐┌┴─────────┐┌┴────────────┐  │  adapters: implement Application ports
│Persistence││Messaging ││ Mavlink  ││  Telemetry  │  │
│ EF Core   ││ RabbitMQ ││ transports││ processing │  │
└──▲───────┘└─▲────────┘└▲─────────┘└▲────────────┘  │
   └──────────┴───────┬──┴───────────┘               │
              ┌───────┴────────┐                     │
              │Gcs.Infrastructure│ composition root: registers all adapters
              └───────▲────────┘                     │
              ┌───────┴────────┐            ┌────────┴──────┐
              │    Gcs.Api     │            │ Gcs.Contracts │ DTOs shared with clients
              └────────────────┘            └────────▲──────┘
                                            ┌────────┴──────┐
                                            │  Gcs.Desktop  │ Avalonia, MVVM; knows only Contracts
                                            └───────────────┘
```

| Project | Responsibility | May depend on |
|---|---|---|
| `Gcs.Domain` | Business rules and invariants (vehicles, missions, commands, alarms) | nothing |
| `Gcs.Contracts` | Public request/response and SignalR message shapes | nothing |
| `Gcs.Application` | Use cases, ports (`IVehicleRepository`, `IMavlinkConnection`, ...), validation | Domain, Contracts |
| `Gcs.Persistence` | EF Core `GcsDbContext`, repositories, migrations, outbox table | Application |
| `Gcs.Messaging` | RabbitMQ connection, outbox publisher, consumers | Application |
| `Gcs.Mavlink` | Transports (UDP, TCP, serial, simulator), connection state machine, command service | Application |
| `Gcs.Telemetry` | Telemetry processing, throttling, latest-state store, batched history writes | Application |
| `Gcs.Infrastructure` | Wires adapters into DI; the only place that knows them all | the adapters |
| `Gcs.Api` | HTTP host: endpoints, SignalR hubs, auth, health checks, logging | Application, Contracts, Infrastructure |
| `Gcs.Simulation` | Simulated vehicle that speaks MAVLink, for development and tests | Mavlink |
| `Gcs.Desktop` | Operator UI (Avalonia, MVVM) | Contracts |

## Vehicle module (Phase 2)

```
POST /api/v1/vehicles
  → VehicleEndpoints            HTTP ↔ use case, ETag/If-Match, problem details
  → RegisterVehicleHandler      validate (FluentValidation calling domain rules), uniqueness, Vehicle.Register
  → Vehicle aggregate           invariants, version, raises VehicleRegistered
  → UnitOfWork / GcsDbContext   one transaction: INSERT vehicles + INSERT outbox_messages
  → OutboxDispatcher (bg)       SELECT ... FOR UPDATE SKIP LOCKED → RabbitMQ (publisher confirms) → mark processed
```

* `Vehicle` holds registration data only. Live link state (`VehicleConnection`, a state machine with bounded
  reconnect attempts) lives in memory and is used by the MAVLink layer from Phase 3.
* Retiring is a soft delete; partial unique indexes reserve callsign and MAVLink system id only for active vehicles.
* Concurrency: the `version` column is an EF Core concurrency token; clients send it back in `If-Match`.

## MAVLink link (Phase 3)

```
POST /api/v1/vehicles/{id}/connection
  → ConnectVehicleHandler      vehicle must exist and be active
  → IVehicleLinkManager        (port) → VehicleLinkManager in Gcs.Mavlink, one MavlinkConnection per vehicle
  → MavlinkConnection          transport session + GCS heartbeat + watchdog + bounded backoff
  → TelemetryTranslator        MAVLink units → domain telemetry → ITelemetrySink (Gcs.Telemetry latest-state store)
GET /api/v1/vehicles/{id}/telemetry → ITelemetryService → latest snapshot (memory, no database)
```

See [mavlink.md](mavlink.md) and [networking.md](networking.md).

## Telemetry (Phase 4)

Latest state in memory, throttled SignalR push, 1 Hz sampled history in PostgreSQL, link events via the outbox.
Every hand-off between the vehicle link and slower consumers goes through a bounded queue or a timer, so nothing
downstream can stall reception. Details: [telemetry.md](telemetry.md); Redis decision: [ADR-011](adr/ADR-011-redis-evaluation-phase-4.md).

## Cross-cutting concerns

| Concern | Implementation |
|---|---|
| Configuration | `appsettings.json` + environment variables (`Section__Key`) + user secrets in development. Options classes are validated at startup (`ValidateOnStart`), so a misconfigured instance fails fast. |
| Secrets | Never committed. Local defaults in `.env` (git-ignored) and `appsettings.Development.json` are development-only. |
| Logging | Serilog, structured, configured from `Serilog` section. Every log line within a request carries `CorrelationId`. |
| Correlation | `X-Correlation-ID` header accepted (validated) or generated, echoed in the response. OpenTelemetry tracing arrives in Phase 9. |
| Health | `/health/live` (process only) and `/health/ready` (PostgreSQL + RabbitMQ). Readiness failure means "do not route traffic", not "restart". |
| API versioning | URL segment (`/api/v1/...`) via `Asp.Versioning`. |
| Errors | RFC 7807 problem details for unhandled errors and status codes. |

## Failure behaviour

| Failure | Behaviour today | Planned |
|---|---|---|
| PostgreSQL down at startup | API starts, `/health/ready` returns 503 | Telemetry history buffered in memory with bounded size (Phase 4) |
| RabbitMQ down | API starts, readiness 503; vehicle changes still succeed and their events wait in the outbox until the broker is back | n/a |
| Transient DB error | EF Core retries (configurable `Persistence:MaxRetryCount`) | n/a |
| Two operators edit the same vehicle | The second save gets `412 Precondition Failed`; nothing is overwritten | n/a |
| Same request sent twice | Register: second gets `409` (callsign in use). Retire: idempotent `204` | Idempotency keys for commands (Phase 7) |
| Vehicle link lost | Reconnecting after 3 s without heartbeat, bounded exponential backoff with jitter, Faulted after max attempts | n/a |
| Vehicle never answers | Faulted after the connect timeout with a readable reason; operator retries | n/a |
| PostgreSQL down during flight | Live telemetry unaffected; history queued in memory (bounded, drop oldest) and written when back | n/a |
| Slow operator client | Its pushes lag; other clients and MAVLink reception unaffected | n/a |

## Related decisions

See [docs/adr](adr/) for the reasoning behind each technology choice.
