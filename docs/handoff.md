# Handoff (2026-10-09)

Where the work stopped and what comes next. Update or remove this file when work resumes.

## State of the branches

| Branch | PR | State |
|---|---|---|
| `main` | | Phases 1–10 (#10 merged 2026-10-08) |
| `feature/phase-11-deployment` | #11 → `main` | Rebased onto `main`, pushed, PR open |

Stale branches: `feature/phase-9-observability` (its PR #8 was merged into `feature/phase-8-security` by mistake; #9
replaced it from `feature/phase-9-observability-main`). Both can be deleted on GitHub.

## Phase 10 fixes made while opening #10

* `MissionEndpointTests` upload test: waits for the vehicle position (takeoff "here" is resolved on upload) and
  expects the takeoff back with coordinates.
* `ci.yml` contained a literal 0x01 byte where the PX4 job's `sed` had `\1`, so GitHub rejected the whole workflow and
  no checks ran. Fixed; check new workflow files with a YAML parser before pushing.

## Phase 11 (Deployment): done

* API: `ReverseProxy:Enabled` + `ReverseProxy:TrustedNetworks` (forwarded headers from the proxy only), integration
  tests `ReverseProxyTests`.
* `deploy/compose.yml`, `deploy/nginx/*`, `deploy/install.sh`, `deploy/tls/internal-ca.sh`, `deploy/backup/*`.
* `deploy/test/verify-install.sh`: Ubuntu 24.04 + systemd + Docker-in-Docker; 17 checks, all passed on 2026-10-09.
* CI job `deploy-config` (shellcheck, compose config, nginx -t). Shellcheck and nginx -t pass locally.
* Docs: `docs/deployment.md`, ADR-018, `docs/learning/phase-11-rehberi.md`, README, security.md.
* Tests: 339 unit, 11 architecture, 128 integration (+1 opt-in SITL skipped), all green.

## Next steps

1. Drive the Phase 11 PR to green CI (new job: Deployment config); Mustafa reviews and merges.
2. Phase 12: advanced networking (several vehicles, ArduPilot SITL over TCP 5760, link quality UI; MAVLink only from
   the vehicles' network via `DOCKER-USER`, MAVLink 2 signing).

## Local machine notes

* `.env` (git-ignored) holds `JWT_SIGNING_KEY` and `GCS_ADMIN_PASSWORD`. The same values are in `dotnet user-secrets`
  for `src/Gcs.Api`.
* The dev stack is not running. Start it with `docker compose --profile sitl up -d --build --wait`.
* The install test containers were removed; the image `gcs-ubuntu-server:24.04` is kept as a cache.
