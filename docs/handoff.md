# Handoff (2026-10-08, end of day)

Where the work stopped and what comes next. Update or remove this file when work resumes.

## State of the branches

| Branch | PR | State |
|---|---|---|
| `main` | | Phases 1–9 (#7 Phase 8 and #9 Phase 9 merged 2026-10-07) |
| `feature/phase-10-px4-sitl` | none yet | Branched from `main` (after #9), pushed, work complete, not yet fully verified |

Stale branches: `feature/phase-9-observability` (its PR #8 was merged into `feature/phase-8-security` by mistake; #9
replaced it from `feature/phase-9-observability-main`). Both can be deleted on GitHub.

## Phase 10 (PX4 SITL): done

* `docker/px4-sitl`: the official `px4io/px4-sitl:v1.18.0-rc1` image (SIH physics, headless, about 50 MB) plus an
  entrypoint that points PX4's GCS link at `GCS_HOST:GCS_PORT` (default `gcs-api:14560`) and runs PX4 with `-d`.
* Compose: service `px4-sitl` in profile `sitl` (system id 10, UDP 14560, home Ankara). The API publishes 14560/udp.
* Flown by hand against the compose stack: arm → takeoff 20 m → 3-waypoint mission → RTL → landing 0.5 m from home.
* Three bugs the real PX4 found, all fixed with unit tests:
  1. NaN air speed (no air speed sensor) made `GET .../telemetry` answer 500. `AirSpeed` is now nullable, and
     non-finite telemetry is dropped in `TelemetryTranslator`.
  2. Unknown message ids were counted as lost frames (76 % loss on a perfect link). Their sequence numbers are now
     tracked (`MavlinkFrameParser`, `MavlinkLinkStatistics`).
  3. A takeoff/land "here" was sent as 0/0, so PX4 flew towards 0° N 0° E. "Here" is resolved on upload
     (`MissionItemMapper`, `vehicle.mission.position_unknown`).
* Opt-in integration test `tests/Gcs.IntegrationTests/Sitl/Px4SitlFlightTests.cs` (`GCS_PX4_SITL=1`,
  trait `Category=Sitl`). It passed 3 out of 3 times locally (about 1 min 45 s each).
* CI job `px4-sitl` ("PX4 SITL flight"), not a required check.
* Docs: `docs/px4-sitl.md`, ADR-017, `docs/learning/phase-10-rehberi.md`, README, missions/networking/mavlink updates,
  `docs/images/px4-sitl-flight-track.svg` (drawn from the real flight's telemetry history).
* Unit tests: 339 green in Release.

## Phase 10: next steps

1. Run the whole suite locally in Release: unit, architecture, and integration (the SITL test is skipped without the
   variable). The full integration suite has not been run since the `AirSpeed` contract change.
2. Open the PR `feature/phase-10-px4-sitl` → **`main`** (not stacked). Drive CI to green, including the new
   `PX4 SITL flight` job on the Linux runner (`host-gateway` mapping, UDP from container to host).
3. Optional: a desktop screenshot of the PX4 vehicle on the map, for the guide. Take it only when nobody else is using
   the screen. Sending keystrokes to the desktop interferes with whatever is in front.

## Next phases

* Phase 11: deployment on Ubuntu Server (TLS proxy, forwarded headers, secrets, OpenTelemetry Collector, backups).
* Phase 12: advanced networking (several vehicles, ArduPilot SITL over TCP 5760, link quality UI).

## Local machine notes

* `.env` (git-ignored) holds `JWT_SIGNING_KEY` and `GCS_ADMIN_PASSWORD`. The same values are in `dotnet user-secrets`
  for `src/Gcs.Api`.
* The Docker stack was stopped with `docker compose --profile sitl down` (volumes kept). Restart with
  `docker compose --profile sitl up -d --build --wait`. The vehicle `PX4-SITL` (system id 10, UDP 14560) is already
  registered in the local database.
