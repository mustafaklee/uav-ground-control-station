# Commands

Operators command a vehicle directly: start the motors, take off, land, return home, change the flight mode. Commands
are short, physical and often irreversible, so the command system is built around four rules
([ADR-014](adr/ADR-014-command-lease-ack-and-audit.md)):

1. **One operator per vehicle.** Only the holder of the vehicle's command lease may command it.
2. **Every command gets an answer.** The GCS waits for the vehicle's `COMMAND_ACK`, retries a bounded number of times,
   and reports Accepted, Rejected (with the vehicle's reason) or TimedOut (state unknown).
3. **Never twice by accident.** The same command is refused while it is still waiting for its answer.
4. **Everything is on record.** Each attempt is written to an append-only audit log before it is sent.

![Desktop control panel: lease, command buttons, last answer and recent commands](images/gcs-desktop-phase7-control.png)

## Commands

| Command | MAVLink | Parameters | Confirmation | Typical refusal |
|---|---|---|---|---|
| `Arm` | `MAV_CMD_COMPONENT_ARM_DISARM` (param1 = 1) | | required | pre-arm checks failed |
| `Disarm` | `MAV_CMD_COMPONENT_ARM_DISARM` (param1 = 0) | | required | `Denied` in flight |
| `Takeoff` | `MAV_CMD_NAV_TAKEOFF` | `altitude`, 2–500 m above home | required | `Denied` when disarmed or flying |
| `Land` | `MAV_CMD_NAV_LAND` (here) | | no (one click) | |
| `ReturnToLaunch` | `MAV_CMD_NAV_RETURN_TO_LAUNCH` | | no (one click) | `Denied` on the ground |
| `SetMode` | `MAV_CMD_DO_SET_MODE` | `mode`, from `GET .../flight-modes` | required | unknown mode → `400` |

PX4 reads the takeoff altitude as metres above mean sea level, ArduPilot as metres above home. Operators always enter
metres above home. For PX4 the GCS adds the home altitude, which it derives from position telemetry.

## API

Every request needs a bearer token ([docs/security.md](security.md)). The operator in leases and audit rows is the
signed-in user. Taking control and sending commands need the `vehicles.command` permission (roles Operator and
Administrator); reading leases, flight modes and the audit log needs only `read`.

| Request | Answer |
|---|---|
| `POST /api/v1/vehicles/{id}/command-lease` | `200` lease (taken or renewed), `409 command.lease_held` |
| `DELETE /api/v1/vehicles/{id}/command-lease` | `204`, `409 command.lease_held` when someone else holds it |
| `GET /api/v1/vehicles/{id}/command-lease` | `200` with `holder` null when free |
| `GET /api/v1/vehicles/{id}/flight-modes` | modes `SetMode` accepts for this autopilot |
| `POST /api/v1/vehicles/{id}/commands` | see below |
| `GET /api/v1/vehicles/{id}/commands?page=1&pageSize=20` | audit log, newest first |

```http
POST /api/v1/vehicles/{id}/commands
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json

{ "command": "Takeoff", "altitude": 30, "confirm": true }
```

| Status | Code | Meaning |
|---|---|---|
| `200` | | the vehicle accepted; body is the audit entry |
| `400` | `command.unknown`, `command.takeoff.altitude`, `command.mode.unknown`, `command.confirmation_required`, `command.operator_required` | nothing was sent or recorded |
| `409` | `command.lease_required`, `command.lease_held` | refused by the GCS, audited as Refused |
| `409` | `command.not_connected`, `command.in_flight`, `command.unsupported` | refused by the GCS, audited as Refused |
| `409` | `command.rejected` | the vehicle answered with a refusal (message names it, e.g. `Denied`) |
| `504` | `command.timed_out` | no answer after every retry. **The vehicle may have executed it.** Check telemetry first |

## Timing

| Setting | Default | Meaning |
|---|---|---|
| `Mavlink:CommandAckTimeoutMilliseconds` | 1500 | wait per transmission |
| `Mavlink:CommandMaxRetries` | 3 | resends; a silent vehicle times out after 4 × 1.5 s = 6 s |
| `Mavlink:CommandInProgressTimeoutMilliseconds` | 10000 | wait after `IN_PROGRESS`, without resending |
| `Commands:LeaseSeconds` | 60 | lease lifetime without activity; the desktop renews every 20 s |

## Audit log

Table `gcs.command_audit`: vehicle id, callsign (copied), operator, command, parameters, source (`GCS-API`), outcome
(`Pending`, `Accepted`, `Rejected`, `TimedOut`, `Refused`), detail, attempts, requested and completed time.

A database trigger enforces the append-only rule. A row may change once, from `Pending` to its outcome. Any other
update and every delete is rejected:

```sql
DELETE FROM gcs.command_audit;  -- ERROR: command_audit is append-only: rows cannot be deleted
```

## Simulator

The built-in simulator (transport `Simulator`, and the `gcs-simulator` container) answers commands like PX4:

```
OnGround ──ARM──► armed ──TAKEOFF──► TakingOff (climbs 3 m/s) ──► Hovering (AUTO.LOITER)
Hovering / circling ──LAND──► Landing (2 m/s) ──► OnGround, disarms itself
Hovering / circling ──RTL──► flies home ──► Landing
DISARM in the air ──► Denied           TAKEOFF while disarmed ──► Denied
```

It starts airborne and circling by default. Set `Simulator__StartAirborne=false` to start on the ground. Tests can make
it lose packets (`DropNextCommands`) or never answer (`IgnoreCommands`).

Try it against the compose stack (vehicle `SIM-01` registered as in the [README](../README.md)):

```bash
token=$(curl -s -X POST localhost:8080/api/v1/auth/login -H "Content-Type: application/json" \
     -d '{"username":"admin","password":"<GCS_ADMIN_PASSWORD from .env>"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
id=<vehicle id>
curl -X POST localhost:8080/api/v1/vehicles/$id/command-lease -H "Authorization: Bearer $token"
curl -X POST localhost:8080/api/v1/vehicles/$id/commands -H "Authorization: Bearer $token" \
     -H "Content-Type: application/json" -d '{"command":"Disarm","confirm":true}'   # 409: Denied in flight
curl -X POST localhost:8080/api/v1/vehicles/$id/commands -H "Authorization: Bearer $token" \
     -H "Content-Type: application/json" -d '{"command":"ReturnToLaunch"}'          # 200: flies home and lands
curl localhost:8080/api/v1/vehicles/$id/commands -H "Authorization: Bearer $token"
```
