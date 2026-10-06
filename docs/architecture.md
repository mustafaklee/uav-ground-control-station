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

## Cross-cutting concerns (Phase 1 state)

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
| RabbitMQ down | API starts, readiness 503, connection created lazily on next use | Outbox keeps events in PostgreSQL until the broker is back (Phase 2+) |
| Transient DB error | EF Core retries (configurable `Persistence:MaxRetryCount`) | n/a |
| Vehicle link lost | n/a | Connection state machine with heartbeat timeout and exponential backoff reconnect (Phase 3) |

## Related decisions

See [docs/adr](adr/) for the reasoning behind each technology choice.
