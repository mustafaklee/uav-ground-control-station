# Missions

A mission is an ordered list of steps a vehicle flies on its own: take off, fly through waypoints, loiter, then
return to launch or land. Operators plan missions in the desktop client, the backend stores and validates them, and
the MAVLink mission protocol carries them to the vehicle.

![Mission planner: route on the map, item list, flyability and upload](images/gcs-desktop-phase6-mission.png)

## Model

| Command | Position | Altitude | Other |
|---|---|---|---|
| `Takeoff` | optional (none means "here", see below) | required | |
| `Waypoint` | required | required | optional `speed` (m/s) from this item on |
| `Loiter` | required | required | `holdSeconds` > 0 |
| `ReturnToLaunch` | none | none | flies to home |
| `Land` | optional (none means "here") | not used | |

Altitudes are metres above home (MAVLink `GLOBAL_RELATIVE_ALT_INT`).

**Field rules.** These are hard errors and are rejected with `400`, keyed per item as `items[3]`:

| Field | Rule |
|---|---|
| Altitude | 2–500 m |
| Hold time | 0–3600 s |
| Speed | 0.5–40 m/s |
| Coordinates | ±90°/±180°, latitude and longitude given together |
| Name | 1–64 characters |
| Items | at most 500 |

**Flyability rules.** These are reported as `issues`, and the mission is still saved:

| Code | Rule |
|---|---|
| `mission.too_short` | at least a takeoff and a final land/RTL |
| `mission.first_not_takeoff` | the first item is a takeoff |
| `mission.last_not_terminal` | the last item is land or RTL |
| `mission.takeoff_not_first` | takeoff appears only first |
| `mission.terminal_not_last` | nothing follows land/RTL |
| `mission.no_waypoints` | at least one waypoint or loiter |

A mission with issues is a draft. It can be saved and edited, but `POST /upload` refuses it with
`409 mission.not_flyable`. [ADR-013](adr/ADR-013-mission-storage-and-validation.md) explains why the rules are split
this way.

## API

| Endpoint | Description |
|---|---|
| `GET /api/v1/missions?page=&pageSize=` | Saved (non-archived) missions, most recently changed first |
| `GET /api/v1/missions/{id}` | One mission with items, issues, distance, last upload; `ETag: "<version>"` |
| `POST /api/v1/missions` | Create → `201` + `Location` + `ETag` |
| `PUT /api/v1/missions/{id}` | Replace name and items; requires `If-Match` (`428` missing, `412 mission.version_mismatch` stale) |
| `DELETE /api/v1/missions/{id}` | Archive (soft delete) → `204` |
| `POST /api/v1/missions/{id}/upload` | `{ "vehicleId": "…" }`: send to a connected vehicle; the result is stored in `lastUpload` |
| `GET /api/v1/vehicles/{id}/mission` | Read the mission currently on the vehicle |

Upload and download errors:

| Error | Meaning |
|---|---|
| `409 vehicle.link.not_connected` | The vehicle is not connected |
| `409 vehicle.mission.transfer_in_progress` | Another transfer to this vehicle is running; one at a time per vehicle |
| `409 vehicle.mission.no_response` | The vehicle stopped answering |
| `409 vehicle.mission.rejected` | The vehicle answered `MISSION_ACK` with an error |
| `409 vehicle.mission.position_unknown` | The mission has a takeoff or land "here", but the vehicle has not reported its position yet |

**"Here" on upload.** MISSION_ITEM_INT has no "unset" value for its integer coordinates, and PX4 takes 0/0 literally
(it would fly towards 0° N 0° E). So the GCS resolves "here" when it uploads: a takeoff gets the vehicle's last
reported position, a land the position of the item before it, which is where the vehicle will be by then. A mission
read back from the vehicle therefore shows these coordinates. Found with PX4 SITL ([px4-sitl.md](px4-sitl.md)).

## MAVLink mission protocol

The receiver drives the transfer by requesting each item, so a lost packet is recovered by asking again:

```
GCS                                   Vehicle
 │── MISSION_COUNT (n) ──────────────►│
 │◄──────────────── MISSION_REQUEST_INT (0)
 │── MISSION_ITEM_INT (0) ───────────►│
 │            …  one request per item  …
 │◄──────────────── MISSION_REQUEST_INT (n-1)
 │── MISSION_ITEM_INT (n-1) ─────────►│
 │◄──────────────── MISSION_ACK (ACCEPTED)
```

Download is the mirror image: the GCS sends `MISSION_REQUEST_LIST`, receives `MISSION_COUNT`, requests each item and
sends the final `MISSION_ACK`.

* **Timeouts and retries.** Each step waits 1.5 s; the last message is re-sent up to 5 times before failing with
  `no_response`. A reply that answers the request resets the counter.
* **One transfer per vehicle.** A gate in `MavlinkConnection` prevents interleaving. Mission replies go to a separate
  inbox, so telemetry keeps flowing during a transfer.
* **Translation (`MissionItemMapper`).**
  * Sequence 0 is a home placeholder, because ArduPilot reserves it and PX4 accepts it.
  * A waypoint's `speed` becomes a `DO_CHANGE_SPEED` item in front of it, and only when the speed changes.
  * `Loiter` maps to `NAV_LOITER_TIME`.
  * On download the reverse mapping folds these back, so upload → download returns the same plan. Tests cover this
    round trip.
* **Messages.** The codec handles all seven messages: `MISSION_COUNT`, `MISSION_REQUEST_INT`, `MISSION_ITEM_INT`,
  `MISSION_ACK`, `MISSION_REQUEST_LIST`, `MISSION_CLEAR_ALL` and `MISSION_CURRENT`. They are verified byte for byte
  against pymavlink golden frames.

The simulator implements the vehicle side, so the whole path (desktop → REST → MAVLink over UDP → simulator → back)
runs without hardware.

## Desktop planner

The **Mission** tab next to **Flight**:

1. **New** starts a plan with takeoff (30 m) and RTL, and turns on *Click map to add waypoints*.
2. Each map click inserts a waypoint before the final RTL/Land, at the previous waypoint's altitude (50 m by default).
   Clicks that move the mouse more than 4 px count as panning, so dragging the map never adds a point.
3. Select a row to edit its command, position, altitude, hold and speed. **Up**, **Down** and **Remove** reorder the
   plan. The route and its distance update while you type. Number boxes accept `39.93` and `39,93`.
4. **Save** creates or updates the mission (with `If-Match`). Server issues appear under each row and in *Checks*.
5. **Upload to vehicle** saves first, then uploads to the vehicle selected in the Flight tab, but only when the mission
   is flyable.
6. **Read from vehicle** loads the vehicle's current mission as an unsaved copy.

Opening a mission zooms the map to its route and turns off *Follow vehicle*.

## Link restore

Phase 6 also made vehicle links survive an API restart. Connect and disconnect store the operator's intent
(`vehicles.link_requested`). On startup, `VehicleLinkRestorer` reconnects every active vehicle that was connected
before. Retiring a vehicle clears the flag.

## Not yet

* Starting, pausing and cancelling a mission, and showing the current mission item, are vehicle commands. They arrive
  with the command system in Phase 7, behind authorization and audit.
* Dragging waypoints on the map, survey and grid patterns, terrain-relative altitude, geofence and rally points.
