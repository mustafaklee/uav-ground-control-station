# PX4 SITL integration

The built-in simulator answers like PX4, but it is our own code: it can only behave the way we already expect. In Phase 10
a **real PX4 autopilot** flies against the GCS. It runs as software-in-the-loop (SITL) in a container, without a
display. The flow from the brief now works end to end:

```
PX4 SITL (container) ──MAVLink v2 / UDP──► GCS API ──SignalR──► Avalonia GCS
        ▲                                     │
        └────────── commands, mission ────────┘
```

![PX4 SITL flight recorded by the GCS](images/px4-sitl-flight-track.svg)

The picture is drawn from `GET /api/v1/vehicles/{id}/telemetry/history`, so it shows what the GCS recorded, not what PX4
planned. The decisions behind this phase are in [ADR-017](adr/ADR-017-px4-sitl.md).

## What runs

| Part | Value |
|---|---|
| Image | `px4io/px4-sitl:v1.18.0-rc1`, the official PX4 SITL image with SIH physics (about 50 MB) |
| Our layer | [`docker/px4-sitl`](../docker/px4-sitl): an entrypoint that points PX4's GCS link at the API, and `-d` (no interactive shell) |
| Vehicle | SIH quadcopter (`PX4_SIM_MODEL=sihsim_quadx`), home in Ankara (39.925533, 32.866287, 938 m) |
| MAVLink | system id 10, sends to `gcs-api:14560/udp`; the GCS answers to the address PX4 sent from |
| Start-up | ready to arm about 10 s after start, once the estimator has converged |

SIH ("simulation in hardware") computes the flight physics inside PX4 itself. Gazebo would add 3D graphics and sensors
(cameras, lidar) that a GCS does not need, at the cost of a multi-gigabyte image and a GPU-friendly host.

## Run it with the compose stack

The SITL service is in the `sitl` profile, so a plain `docker compose up` does not start it.

```bash
docker compose --profile sitl up -d --build --wait
```

Register and connect it once (the desktop client can do the same in the fleet list):

```bash
token=$(curl -s -X POST localhost:8080/api/v1/auth/login -H "Content-Type: application/json" \
     -d '{"username":"admin","password":"<GCS_ADMIN_PASSWORD from .env>"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
id=$(curl -s -X POST localhost:8080/api/v1/vehicles -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
     -d '{"callsign":"PX4-SITL","mavlinkSystemId":10,"autopilot":"Px4","type":"Multirotor",
          "connection":{"transport":"Udp","host":"0.0.0.0","port":14560}}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -s -X POST localhost:8080/api/v1/vehicles/$id/connection -H "Authorization: Bearer $token"
```

Then fly it from the desktop client: take control, **Arm**, **Takeoff**, upload a mission from the planner, set
`AUTO.MISSION`, and **RTL**. With curl, the same steps are the requests in [commands.md](commands.md#api) and
[missions.md](missions.md#api).

PX4 SITL against an API started with `dotnet run` on the host also works: run the container with
`-e GCS_HOST=host.docker.internal` (Docker Desktop resolves that name to the host).

## The automated flight test

`tests/Gcs.IntegrationTests/Sitl/Px4SitlFlightTests.cs` flies the whole scenario through the public API, against the
in-memory API of the integration tests and a PX4 container started by Testcontainers:

1. Register, connect, wait for a 3D GPS fix. Air speed must be `null` (a quad has no air speed sensor).
2. Take the command lease, **Arm** (repeated every 2 s until the pre-arm checks pass), **Takeoff** to 20 m.
3. Save and upload a three-waypoint mission whose takeoff item has no position. Download it again: the takeoff must
   carry the home coordinates, never 0/0.
4. **SetMode AUTO.MISSION** and check that the vehicle heads north, towards waypoint 1.
5. **ReturnToLaunch** 60 m out. PX4 must switch to `AUTO.RTL`, land within 5 m of home and disarm itself.
6. The audit log shows Arm, Takeoff, SetMode and ReturnToLaunch as Accepted. Packet loss is below 5 % with no CRC errors.

It takes about two minutes and is opt-in:

```bash
GCS_PX4_SITL=1 dotnet test --project tests/Gcs.IntegrationTests -- --filter-trait "Category=Sitl"
```

```powershell
$env:GCS_PX4_SITL = "1"; dotnet test --project tests/Gcs.IntegrationTests -- --filter-trait "Category=Sitl"
```

Without the variable, the test reports itself as skipped. CI runs it in its own job, **PX4 SITL flight**. That job is
not a required check, so a slow PX4 download cannot block a merge, but a red result is a real regression.

How the container reaches the test API: the API runs inside the test process on the host, so PX4 sends to
`host.docker.internal`. Docker Desktop provides that name. On Linux (CI), the test maps it to the host with
`--add-host host.docker.internal:host-gateway`.

## What the real autopilot found

The built-in simulator never showed these three bugs. All three are fixed, with unit tests:

| Symptom | Cause | Fix |
|---|---|---|
| `GET .../telemetry` answered `500` | PX4 sends `VFR_HUD.airspeed = NaN` when there is no air speed sensor; NaN cannot be written as JSON | `airSpeed` is nullable; messages with NaN/infinity in required fields are dropped at the MAVLink boundary |
| 76 % packet loss on a perfect link | frames with message ids we do not decode were skipped without tracking their sequence numbers, so each looked like a gap | skipped frames still update the sequence tracking and count as arrived |
| Mission flew south-west instead of north | a takeoff "here" was sent as latitude/longitude 0/0; PX4 takes it literally ("first waypoint 5548 km from home") | "here" is resolved on upload: takeoff gets the vehicle's position, land the position of the item before it |

## PX4 behaviour worth knowing

| Behaviour | What the GCS sees |
|---|---|
| Arming right after start | `Rejected` until pre-arm checks pass (about 10 s). Retry. |
| `AUTO.MISSION` right after an upload | `TemporarilyRejected` for a moment while PX4 checks the new mission. Retry after a second or two. |
| Takeoff altitude | PX4 reads it as metres above mean sea level; the GCS adds the home altitude ([commands.md](commands.md)) |
| Mission takeoff item while already airborne | skipped; PX4 continues with the first waypoint |
| Landing | PX4 disarms itself a few seconds after touchdown (`Disarmed by landing`) |
| Disarm on the ground | PX4 disarms by itself after about 10 s armed without taking off |
| Many message types | PX4 streams about 35 message types, some at 50 Hz. The GCS decodes what it needs and skips the rest |

## Troubleshooting

| Problem | Check |
|---|---|
| Link stays `Connecting` | `docker logs gcs-px4-sitl-1` should print `MAVLink GCS link -> gcs-api (...):14560/udp`. The vehicle must be registered on port 14560 with system id 10. |
| `409` "Another active vehicle already uses this MAVLink system id" | Another active vehicle uses id 10. Retire it, or set `PX4_PARAM_MAV_SYS_ID` to a free id in `docker-compose.yml`. |
| PX4 sends to a stale address | `gcs-api` was recreated with a new IP after PX4 started. `docker compose --profile sitl restart px4-sitl` |
| Arming keeps being rejected | `docker logs gcs-px4-sitl-1` names the failing pre-arm check |
