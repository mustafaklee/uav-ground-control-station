# ADR-004: PostgreSQL with EF Core as the system of record

* Status: Accepted
* Date: 2026-10-06

## Context

We store vehicles, missions, users, roles, audit logs and historical telemetry. Audit and mission data need
transactions and strong consistency; telemetry history is append-heavy and queried by vehicle and time range.

## Decision

Use **PostgreSQL** (image `postgres:18-alpine`) through **EF Core 10 + Npgsql**. All tables live in the `gcs`
schema. Transient failures are retried by the EF Core execution strategy. Schema changes are made with EF Core
migrations (strategy confirmed in Phase 2). High-rate telemetry is **not** written per message; it is sampled and
batch-inserted (Phase 4).

## Alternatives considered

* **SQL Server**: equally capable, but production licensing cost and a weaker fit for Linux-first deployment.
* **MongoDB**: flexible schema, but audit and mission data are relational and need transactions.
* **Dedicated time-series DB (InfluxDB, TimescaleDB)**: better for very large telemetry volumes. TimescaleDB is a
  PostgreSQL extension, so it stays an upgrade path without changing the database.

## Consequences

* Open source, no licence cost, first-class on Linux and in containers.
* Integration tests run against a real PostgreSQL container (Testcontainers), not an in-memory fake.
* JSONB is available for semi-structured data such as raw autopilot parameters.
