# ADR-017: PX4 SITL with SIH in Docker, an opt-in flight test and a separate CI job

* Status: Accepted
* Date: 2026-10-08
* Phase: 10

## Context

Until Phase 9, every MAVLink test talked to our own simulator (`Gcs.Mavlink.Simulation`). It is fast and
deterministic, but it was written from our understanding of PX4. A bug in that understanding shows up in the GCS and in
the simulator in the same way, so the tests stay green. The brief asks for the real chain:
PX4 SITL → MAVLink UDP → backend → SignalR → Avalonia, plus a takeoff → mission → RTL scenario.

Questions to decide:

1. Which PX4 SITL build and simulator, and how to run it without a display?
2. How does the container reach the GCS, both in compose and in tests?
3. What runs automatically, and where?

## Decision

### 1. The official `px4io/px4-sitl` image with SIH physics, pinned to a tag

| Option | Size | Start-up | Notes |
|---|---|---|---|
| Build PX4 from source in a dev image | about 1.4 GB plus a 10–15 min build | slow | full control, slow CI |
| `jonasvautherin/px4-gazebo-headless` (Gazebo Classic) | about 3.3 GB compressed | about 30 s | community image; 3D world we do not use |
| **`px4io/px4-sitl` (SIH)** | **about 50 MB** | **a few seconds** | official, headless by design, amd64 and arm64 |

SIH ("simulation in hardware") runs the flight physics as a PX4 module. A GCS needs MAVLink behaviour (modes,
acknowledgements, mission protocol, failsafes), not rendered sensors, so SIH tests what matters at a fraction of the
cost. Gazebo stays an option for a later payload or camera phase.

The tag is `v1.18.0-rc1`. This image has only been published since the 1.18 development cycle, so no stable 1.18 tag
exists yet. A fixed tag still beats `latest`, which is rebuilt from PX4's main branch and could change our test results
overnight. Move to `v1.18.0` when it is published.

### 2. A thin entrypoint that points PX4's GCS link at the API

PX4's GCS link sends to `127.0.0.1:14550`, which inside a container is the container itself. The upstream entrypoint
only rewrites it to `host.docker.internal`. Our `docker/px4-sitl/entrypoint.sh`:

* resolves `GCS_HOST` (default `gcs-api`) to IPv4, because PX4's `mavlink -t` accepts only an IP address,
* adds `-t <ip> -o <GCS_PORT>` to the GCS link line in `px4-rc.mavlink`,
* starts PX4 with `-d`. Without it PX4 opens its `pxh>` shell, which spins on a closed stdin and wrote 40 MB of
  prompts to the log in a minute.

Ports and ids: SITL uses **14560/udp and system id 10**, so it runs next to the built-in simulator (14550, id 1)
without a clash. The address is resolved once at start-up. If `gcs-api` is recreated with a new IP, `px4-sitl` must be
restarted. This is documented, and it is acceptable for a development tool.

In compose, the service is in the **`sitl` profile**. Most work does not need a second vehicle, and CI's compose smoke
test should not download PX4.

### 3. An opt-in xUnit test and its own CI job

The scenario is an **integration test** (`Px4SitlFlightTests`), not a shell script:

* It reuses the integration-test API (in memory, real PostgreSQL and RabbitMQ) and the same contracts as the client,
  so a renamed field fails to compile instead of failing at run time.
* Testcontainers builds `docker/px4-sitl` and starts it. PX4 sends to `host.docker.internal`, which the test maps to
  `host-gateway` so it also resolves on Linux runners.
* The test asserts outcomes (mode changes, position, landing within 5 m, audit entries, link quality), not timings.
  PX4's legitimate "not yet" answers (arming before pre-arm checks pass, `AUTO.MISSION` while a new mission is being
  checked) are retried every 2 s, as an operator would.
* **Opt-in** with `GCS_PX4_SITL=1`. Without it, the test reports as skipped. This keeps `dotnet test` fast and
  independent of Docker Hub for everyday work.

CI runs it in a separate job, **PX4 SITL flight**, after build-and-test. It is not a required check: a Docker Hub
outage should not block merges, but a red run is investigated like any failure. It can become required once it has
been stable for a while.

### 4. Fixes made because the real autopilot disagreed with our simulator

| Finding | Decision |
|---|---|
| `VFR_HUD.airspeed` is NaN without an air speed sensor; NaN broke JSON serialization (500) | `MotionState.AirSpeed` / `MotionDto.AirSpeed` become `double?`. The MAVLink translator is the boundary: NaN means unknown, and a message whose required values are not finite is dropped |
| Skipped (unknown) message ids were counted as lost frames (76 %) | Their sequence numbers are tracked. The loss ratio is lost / (decoded + skipped + lost) |
| Takeoff/land "here" was sent as 0/0, which PX4 takes literally | "Here" is resolved on upload: takeoff gets the vehicle's last position, land the position of the item before it. Upload fails with `vehicle.mission.position_unknown` when the position is needed but unknown. ArduPilot accepts explicit coordinates too, so the behaviour stays autopilot neutral |

Changing `AirSpeed` to nullable is a contract change. The desktop client shows "—" for it. Persistence already stored
it as nullable.

## Consequences

* Good: the GCS is now tested against the autopilot it is mainly built for, and the first run found three real bugs.
* Good: a 50 MB image and about two minutes per flight make the real autopilot cheap enough for every pull request.
* Good: the simulator stays the fast, deterministic default for unit and most integration tests, and the place for
  fault injection (dropped and ignored commands) that PX4 cannot produce on demand.
* Bad: SITL time is wall-clock time. The flight test cannot be made faster without PX4's speed factor, which also
  changes failsafe timings. We keep 1×.
* Bad: one more external image to keep pinned and updated. Renovate or Dependabot can manage the `FROM` line later.
* Not covered: ArduPilot SITL (TCP 5760, system id 1, different mode numbers). The abstraction supports it. It is a
  candidate for Phase 12 together with multiple vehicles.
