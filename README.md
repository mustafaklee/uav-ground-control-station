# UAV Ground Control Station

[![CI](https://github.com/mustafaklee/uav-ground-control-station/actions/workflows/ci.yml/badge.svg)](https://github.com/mustafaklee/uav-ground-control-station/actions/workflows/ci.yml)

A modular, testable ground control station (GCS) for managing multiple UAVs over MAVLink, built with .NET 10,
ASP.NET Core, PostgreSQL, RabbitMQ, SignalR and Avalonia UI. It is engineered like a defence-industry product:
reliability, safety, security and observability come before features.

> **Status: Phase 2, vehicle domain.** Vehicles can be registered, listed, updated and retired through a versioned REST API
> backed by PostgreSQL, with optimistic concurrency and domain events delivered to RabbitMQ through an outbox.
> MAVLink, telemetry and UI features arrive in the phases listed in the [roadmap](#roadmap).

## Project overview

The GCS lets operators register vehicles, connect to them over UDP, TCP or serial MAVLink links, watch live telemetry
on a map, plan and upload missions, and send commands (arm, takeoff, mode change, RTL, land) with role-based
authorization and a full audit trail. It runs without hardware against a built-in simulator or PX4 SITL.

## Architecture

Modular monolith with Clean Architecture. See [docs/architecture.md](docs/architecture.md) and the
[architecture decision records](docs/adr/README.md).

```
Avalonia GCS ──REST/SignalR──► Gcs.Api ──► Application ──► Domain
                                  │
                         Infrastructure (composition root)
              ┌──────────────┬────┴─────────┬───────────────┐
         Persistence      Messaging       Mavlink         Telemetry
         (PostgreSQL)    (RabbitMQ)   (UDP/TCP/serial)   (live state)
                                           │
                               PX4 / ArduPilot / SITL / Simulator
```

## Features

| Area | Status |
|---|---|
| Solution structure, analyzers, central package management | ✅ Phase 1 |
| Docker Compose (PostgreSQL, RabbitMQ, Redis, API) | ✅ Phase 1 |
| Structured logging with correlation ids, liveness/readiness health checks | ✅ Phase 1 |
| CI: build, unit, architecture and integration tests, Docker build, compose smoke test | ✅ Phase 1 |
| Vehicle management API (CRUD, paging, ETag concurrency, retire) | ✅ Phase 2 |
| Domain events via transactional outbox → RabbitMQ | ✅ Phase 2 |
| Database migrations (dev: on startup, Docker: migrator container) | ✅ Phase 2 |
| MAVLink transports, simulator, connection lifecycle | Planned (Phase 3) |
| Real-time telemetry over SignalR | Planned (Phase 4) |
| Avalonia operator UI | Planned (Phase 5) |
| Mission planner | Planned (Phase 6) |
| Command system with authorization | Planned (Phase 7) |
| Authentication, roles, audit log, rate limiting | Planned (Phase 8) |
| OpenTelemetry metrics and tracing | Planned (Phase 9) |
| PX4 SITL integration | Planned (Phase 10) |

## Technology stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# |
| Backend | ASP.NET Core minimal APIs, API versioning (`Asp.Versioning`) |
| Persistence | PostgreSQL 18, EF Core 10, Npgsql |
| Messaging | RabbitMQ 4 (domain events, outbox) |
| Real time | SignalR |
| Desktop | Avalonia UI 12, CommunityToolkit.Mvvm |
| MAVLink | Asv.Mavlink (behind our own abstractions) |
| Logging | Serilog |
| Testing | xUnit v3 (Microsoft.Testing.Platform), Shouldly, NetArchTest, Testcontainers |
| CI | GitHub Actions |

## Getting started

### Prerequisites

* [.NET SDK 10.0.401+](https://dotnet.microsoft.com/download) (pinned in `global.json`)
* [Docker Desktop](https://www.docker.com/products/docker-desktop/) (WSL 2 backend on Windows) or Docker Engine on Linux
* Git

### Local development

```bash
git clone https://github.com/mustafaklee/uav-ground-control-station.git
cd uav-ground-control-station
cp .env.example .env              # adjust credentials if you like
docker compose up -d --wait postgres rabbitmq
dotnet build Gcs.slnx
dotnet run --project src/Gcs.Api  # http://localhost:5134/health/ready
```

### Database migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Gcs.Persistence --startup-project src/Gcs.Persistence
```

In Development the API applies migrations on startup. In Docker the `gcs-migrator` container applies them before the
API starts. See [ADR-009](docs/adr/ADR-009-database-migrations.md).

### Docker setup

Run the whole backend stack in containers:

```bash
docker compose up -d --build --wait
curl http://localhost:8080/health/ready
```

| Service | URL |
|---|---|
| API | http://localhost:8080 |
| RabbitMQ management | http://localhost:15672 |
| PostgreSQL | localhost:5432 |
| Redis (unused until Phase 4) | localhost:6379 |

`gcs-migrator` runs once per `up`, applies migrations and exits with code 0.

All ports are bound to `127.0.0.1`.

## Configuration

Configuration follows the standard ASP.NET Core order: `appsettings.json` → `appsettings.{Environment}.json` →
user secrets (Development) → environment variables. Nested keys use `__` in environment variables.

| Key | Purpose |
|---|---|
| `ConnectionStrings__Postgres` | PostgreSQL connection string (required) |
| `Persistence__CommandTimeoutSeconds`, `Persistence__MaxRetryCount` | EF Core command timeout and transient retry count |
| `RabbitMq__HostName`, `__Port`, `__VirtualHost`, `__UserName`, `__Password` | Broker connection (user name and password required) |
| `Persistence__ApplyMigrationsOnStartup` | Apply migrations when the API starts (Development/tests only) |
| `Outbox__Enabled`, `Outbox__PollingIntervalMilliseconds`, `Outbox__BatchSize` | Outbox dispatcher |
| `RabbitMq__EventsExchange` | Topic exchange for domain events (default `gcs.events`) |
| `Serilog__MinimumLevel__Default` | Log level |

Options are validated at startup; an invalid configuration stops the API immediately. Secrets are never committed:
use `.env` (git-ignored), user secrets or the deployment secret store.

## Testing

```bash
dotnet test --solution Gcs.slnx                 # everything
dotnet test --project tests/Gcs.UnitTests
dotnet test --project tests/Gcs.ArchitectureTests
dotnet test --project tests/Gcs.IntegrationTests  # needs Docker (Testcontainers)
```

* **Unit tests**: domain building blocks and pure logic.
* **Architecture tests**: enforce the layer dependency rules.
* **Integration tests**: run the API in memory against real PostgreSQL and RabbitMQ containers.

## Deployment

Planned for Phase 11: Ubuntu Server with Nginx (TLS termination), the API container, PostgreSQL, RabbitMQ and Redis.

## MAVLink integration

Planned for Phase 3. Transports (UDP, TCP, serial, simulator) sit behind `IMavlinkTransport` / `IMavlinkConnection`;
the UI and API never touch sockets or packets. See [ADR-007](docs/adr/ADR-007-mavlink-abstraction.md).

## PX4 SITL integration

Planned for Phase 10: PX4 SITL → MAVLink UDP → GCS backend → SignalR → Avalonia GCS.

## API documentation

In Development the OpenAPI document is served at `/openapi/v1.json`.

| Endpoint | Description |
|---|---|
| `GET /health/live` | Process is running (no dependency checks) |
| `GET /health/ready` | PostgreSQL and RabbitMQ reachable |
| `GET /api/v1/system/info` | Service name, version and environment |
| `GET /api/v1/vehicles?page=&pageSize=&status=&search=` | List vehicles (paged; filter by `Active`/`Retired`, search callsign) |
| `GET /api/v1/vehicles/{id}` | One vehicle; response carries `ETag: "<version>"` |
| `POST /api/v1/vehicles` | Register a vehicle → `201 Created` + `Location` + `ETag` |
| `PUT /api/v1/vehicles/{id}` | Update; requires `If-Match: "<version>"` (`428` if missing, `412` if stale) |
| `DELETE /api/v1/vehicles/{id}` | Retire (soft delete, idempotent) → `204` |

Example:

```bash
curl -i -X POST http://localhost:8080/api/v1/vehicles -H "Content-Type: application/json" -d '{
  "callsign": "UAV-01", "mavlinkSystemId": 1, "autopilot": "Px4", "type": "Multirotor",
  "connection": { "transport": "Udp", "host": "127.0.0.1", "port": 14550 }
}'
```

Errors are RFC 7807 problem documents with a stable `code` (e.g. `vehicle.callsign.in_use`, `vehicle.version_mismatch`).
Published events go to the `gcs.events` topic exchange with routing keys such as `vehicle.registered`.

## Security

Phase 1 baseline: no secrets in the repository, non-root container user, services bound to localhost, validated
configuration, sanitized correlation ids. JWT authentication, refresh tokens, role and policy based authorization,
rate limiting, secure headers, HTTPS and audit logging arrive in Phase 8.

## Roadmap

| Phase | Scope |
|---|---|
| 0 | Analysis ✅ |
| 1 | Project foundation ✅ |
| 2 | Vehicle domain, persistence, CRUD API ✅ |
| 3 | MAVLink abstraction, UDP transport, simulator, heartbeat, connection lifecycle |
| 4 | Telemetry processing, latest state, SignalR |
| 5 | Avalonia GCS |
| 6 | Mission planner |
| 7 | Command system |
| 8 | Security |
| 9 | Observability |
| 10 | PX4 SITL |
| 11 | Deployment |
| 12 | Advanced networking |

## Contributing

* Branch from `main`; open a pull request. CI must pass before merging.
* Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `test:`, `docs:`, `ci:`, `chore:`).
* Significant technical decisions get an ADR in `docs/adr`.

## License

Not yet decided. All rights reserved until a license is added.
