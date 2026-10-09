# Handoff (2026-10-09, project complete)

All twelve phases of the plan are on `main`. There is no work in progress and no open pull request except this
documentation one.

## What exists

* Backend (`src/Gcs.Api` and its layers), desktop client (`src/Gcs.Desktop`), standalone simulator (`src/Gcs.Simulation`).
* Local stack: `docker-compose.yml` (PostgreSQL, RabbitMQ, Redis, Aspire dashboard, migrator, API, simulator; profile
  `sitl` adds PX4 SITL).
* Server install: `deploy/` (Ubuntu 24.04, Nginx, UFW, TLS, backups), verified by `deploy/test/verify-install.sh`.
* 19 ADRs, 12 phase guides plus `docs/learning/genel-bakis.md` (Turkish overview).

## Verified on `main` (4a9aa6a, 2026-10-09)

* Unit 379, architecture 11, integration 131 + 1 opt-in: all green in Release.
* `GCS_PX4_SITL=1` flight test (takeoff → mission → RTL with a real PX4): green.
* CI on every PR: Build and test, Docker smoke test, PX4 SITL flight, Deployment config.

## How to run

* Locally: README → Getting started (`docker compose up -d --build --wait`, then `dotnet run --project src/Gcs.Desktop`).
* On a server: `docs/deployment.md` (`sudo deploy/install.sh --domain … --tls letsencrypt|internal`).

## Known limitations

See README → Known limitations: no serial transport, unsigned MAVLink, single API instance, no instant token
revocation, online map tiles, untuned link-quality thresholds, Let's Encrypt path not exercised end to end.

## Possible next steps (need Mustafa's decision)

1. Field readiness: offline map tiles and a serial transport for USB telemetry radios.
2. MAVLink 2 message signing, and MAVLink accepted only from the vehicles' network (`DOCKER-USER` rules).
3. Container images published to GHCR from CI, so servers pull instead of build; a staging install in CI.
4. ArduPilot SITL (TCP 5760) flight test next to PX4, and a multi-vehicle SITL scenario.
5. A second API instance: shared leases and rate limits (Redis), vehicle ownership per instance.

## Local machine notes

* `.env` (git-ignored) holds `JWT_SIGNING_KEY` and `GCS_ADMIN_PASSWORD`; the same values are in `dotnet user-secrets`
  for `src/Gcs.Api`. `claude_prompt.txt` is the original brief and stays out of git.
* No Docker stack is running. Volumes are kept.
* Merged branches that can be deleted on GitHub: `feature/phase-9-observability`, `feature/phase-9-observability-main`,
  `feature/phase-10-px4-sitl`, `feature/phase-11-deployment`, `feature/phase-12-advanced-networking`.
