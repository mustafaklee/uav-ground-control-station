# Handoff (2026-10-07, end of day)

Where the work stopped and what comes next. Update or remove this file when work resumes.

## State of the branches

| Branch | PR | State |
|---|---|---|
| `main` | | Phases 1–7 (PR #6 merged 2026-10-07) |
| `feature/phase-8-security` | [#7](https://github.com/mustafaklee/uav-ground-control-station/pull/7) → `main` | Done, CI green, waiting for review and merge |
| `feature/phase-9-observability` | none yet | Branched from `feature/phase-8-security`, pushed, all local tests green |

**Before opening the Phase 9 PR:** once #7 is merged into `main`, rebase the Phase 9 branch onto `main`. Then open its
PR against `main`. PRs #3/#4 once went into stacked branches instead of `main`, so do not repeat that.

```bash
git fetch origin main
git rebase --onto origin/main feature/phase-8-security feature/phase-9-observability
git push --force-with-lease origin feature/phase-9-observability
```

## Phase 9 (observability): done

* OpenTelemetry tracing and metrics in the API (ASP.NET Core, HttpClient, Npgsql, runtime). Our code uses
  `System.Diagnostics` only.
* Custom spans: `vehicle.command {Type}` (with one event per COMMAND_LONG transmission), `mission.upload/download`,
  `{Event} publish`.
* Trace context through the outbox: `outbox_messages.trace_parent` (migration `AddOutboxTraceParent`). The publish
  span continues the request's trace, and `traceparent` goes into the RabbitMQ message headers.
* `BackgroundNoiseSampler` drops parentless client spans (outbox polling, history batches).
* Metrics: `gcs.commands`, `gcs.command.duration`, `gcs.mission.transfers`, `gcs.mavlink.frames`,
  `gcs.link.state_changes`, `gcs.links`, `gcs.auth.logins`.
* Serilog: trace id in the console template, OTLP log export when `Observability:OtlpEndpoint` is set.
* Health: `/health/details` (needs `read`) with `vehicle-links`, `outbox-backlog` and `telemetry-history` checks
  (Degraded with data).
* Compose: the Aspire dashboard (`otel-dashboard`, UI http://localhost:18888). The API exports to it.
* Docs: ADR-016, `docs/observability.md`, `docs/learning/phase-9-rehberi.md`, README.
* Tests: unit 333, architecture 11, integration 126 (including the trace chain, outbox → RabbitMQ trace context,
  health details and metrics). All green locally in Release.

## Phase 9: left to do

1. Rebase onto `main` after #7 merges (above), open the PR, and drive CI green. The CI compose smoke test now also
   starts the dashboard container. If pulling it is slow in CI, consider a compose profile.
2. Optional: a CI smoke step that checks `/health/details` with the admin token.
3. Optional: instrument the simulator container (it does not export telemetry today).

## Next phases

* Phase 10: PX4 SITL in Docker, talking MAVLink/UDP to the API (replaces the built-in simulator for realistic tests).
* Phase 11: deployment on Ubuntu Server (TLS proxy, forwarded headers, secrets, OpenTelemetry Collector with durable
  backends, backups).
* Phase 12: advanced networking.

## Local machine notes

* `.env` (git-ignored) holds `JWT_SIGNING_KEY` and `GCS_ADMIN_PASSWORD`. The same values are in `dotnet user-secrets`
  for `src/Gcs.Api`.
* The Docker stack is running with the Phase 9 code. Users: `admin` (Administrator), `mustafa` (Operator) and
  `gozlemci` (Observer). Their password is `GCS_ADMIN_PASSWORD` from `.env`; change it after the first sign-in.
