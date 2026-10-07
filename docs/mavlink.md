# MAVLink integration

How the GCS talks to vehicles. Decisions: [ADR-007](adr/ADR-007-mavlink-abstraction.md) (abstraction) and
[ADR-010](adr/ADR-010-own-mavlink-codec.md) (own codec, verified against pymavlink).

## Layers

```
 API / use cases          IVehicleLinkManager, ITelemetryService      (Gcs.Application: no MAVLink types)
        │
 VehicleLinkManager       one MavlinkConnection per vehicle            (Gcs.Mavlink.Connections)
        │
 MavlinkConnection        session loop: heartbeat, watchdog, backoff, VehicleConnection state machine
        │       └─────►   TelemetryTranslator → ITelemetrySink (Gcs.Telemetry latest-state store)
 MavlinkFrameParser /     bytes ⇄ frames ⇄ messages                    (Gcs.Mavlink.Protocol)
 MavlinkCodec
        │
 IMavlinkTransport        UDP · TCP · Simulator (in-process) · Serial (planned)   (Gcs.Mavlink.Transports)
```

The UI and API never see sockets, frames or MAVLink enums. They see `ConnectionState`, `TelemetrySnapshot`
(degrees, metres, m/s) and flight mode names.

## Wire format (MAVLink 2)

| Offset | Field | Notes |
|---|---|---|
| 0 | STX `0xFD` | `0xFE` for MAVLink 1 (also accepted) |
| 1 | payload length | after trailing-zero truncation |
| 2 | incompat flags | `0x01` = signed (13-byte signature follows the CRC) |
| 3 | compat flags | |
| 4 | sequence | per sender, wraps 255 → 0; gaps = lost frames |
| 5 | system id | which vehicle (1–255) |
| 6 | component id | 1 = autopilot, 190 = GCS |
| 7–9 | message id | 24-bit little endian |
| 10… | payload | fields sorted by size, little endian |
| … | CRC-16/MCRF4XX | over bytes 1…end of payload, then the message's CRC_EXTRA |

## Supported messages

| Message | Id | Direction | Used for |
|---|---|---|---|
| HEARTBEAT | 0 | both | link liveness, type, autopilot, arm state, flight mode |
| SYS_STATUS | 1 | vehicle → GCS | battery voltage/current/remaining |
| GPS_RAW_INT | 24 | vehicle → GCS | fix type, satellites |
| ATTITUDE | 30 | vehicle → GCS | roll, pitch, yaw |
| GLOBAL_POSITION_INT | 33 | vehicle → GCS | lat/lon, altitude MSL and relative |
| VFR_HUD | 74 | vehicle → GCS | ground/air speed, climb, heading |
| COMMAND_LONG | 76 | GCS → vehicle | commands: ARM, DISARM, TAKEOFF, LAND, RTL, SET_MODE (see [commands.md](commands.md)) |
| COMMAND_ACK | 77 | vehicle → GCS | command results; resend with `confirmation` + 1 on timeout |

Every message is covered by a golden test: `scripts/generate-mavlink-golden.py` produces reference frames with
pymavlink, and the codec must encode to exactly those bytes and decode them back.

**Adding a message:** add a record in `Protocol/Messages/Messages.cs` (wire offsets!), register it with its CRC_EXTRA in
`MavlinkMessageRegistry`, add a case to the generator, regenerate `GoldenFrames.g.cs`, add the golden test case.

## Link lifecycle

```
Disconnected ──connect──► Connecting ──first heartbeat──► Connected ◄──────────────┐
                              │ no heartbeat in 10 s          │ no heartbeat for 3 s   │ heartbeat
                              ▼                               ▼                        │
                           Faulted ◄──max attempts── Reconnecting ──backoff 1,2,4,8… s──┘
                              │
                              └── operator "connect" again ──► Connecting
```

| Setting (`Mavlink:` section) | Default | Meaning |
|---|---|---|
| `GcsSystemId` | 255 | system id in the GCS's own HEARTBEAT (component 190) |
| `HeartbeatIntervalMilliseconds` | 1000 | GCS heartbeat rate |
| `HeartbeatTimeoutMilliseconds` | 3000 | link lost after this long without a vehicle heartbeat |
| `ConnectTimeoutMilliseconds` | 10000 | first connect faults after this long without a heartbeat |
| `MaxReconnectAttempts` | 5 | bounded: then Faulted, never an endless loop |
| `ReconnectBaseDelayMilliseconds` / `ReconnectMaxDelayMilliseconds` | 1000 / 30000 | exponential backoff |
| `ReconnectJitterRatio` | 0.2 | ±20 % randomization so many vehicles do not retry in lockstep |

Frames from other system ids (other vehicles, other ground stations) are ignored by a vehicle's connection.

## Flight modes

`custom_mode` means different things per autopilot; `FlightModeDecoder` translates it:

* **PX4:** main mode in byte 2, sub mode in byte 3 (`AUTO` + `LOITER` → `AUTO.LOITER`).
* **ArduPilot:** one number per vehicle family; Copter `5` = `LOITER`, Plane `5` = `FBWA`.

## Simulator

`SimulatedVehicle` is a PX4 quad flying a 150 m circle at 12 m/s and 100 m above home, with physically consistent
position, velocity, heading and bank angle, a draining battery and ARM/DISARM handling. It runs:

* **in-process**: register a vehicle with transport `Simulator`;
* **over UDP**: the `gcs-simulator` container (or `dotnet run --project src/Gcs.Simulation`) sends to the GCS like PX4 SITL.

## Try it

```bash
docker compose up -d --build --wait
# register the vehicle the simulator container impersonates (system id 1), listening on UDP 14550
curl -s -X POST http://localhost:8080/api/v1/vehicles -H "Content-Type: application/json" -d '{
  "callsign":"SIM-01","mavlinkSystemId":1,"autopilot":"Px4","type":"Multirotor",
  "connection":{"transport":"Udp","host":"0.0.0.0","port":14550}}'
curl -s -X POST http://localhost:8080/api/v1/vehicles/<id>/connection
curl -s http://localhost:8080/api/v1/vehicles/<id>/connection   # "state": "Connected"
curl -s http://localhost:8080/api/v1/vehicles/<id>/telemetry    # position, attitude, battery, mode...
```
