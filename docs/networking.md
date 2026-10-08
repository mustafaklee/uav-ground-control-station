# Networking

## Protocols in use

| Path | Protocol | Port | Why |
|---|---|---|---|
| Desktop → API | HTTP(S), REST | 8080 (HTTPS via Nginx in Phase 11) | CRUD, configuration, commands |
| API → Desktop | WebSocket (SignalR) | 8080 | live telemetry push (Phase 4) |
| Vehicle ⇄ API | MAVLink over UDP | 14550/udp (GCS listens) | standard for SITL, companion computers, IP radios |
| PX4 SITL ⇄ API | MAVLink over UDP | 14560/udp (GCS listens) | compose `sitl` profile, next to the simulator on 14550 ([px4-sitl.md](px4-sitl.md)) |
| Vehicle ⇄ API | MAVLink over TCP | e.g. 5760 (ArduPilot SITL) | reliable stream when available |
| API → PostgreSQL | PostgreSQL wire protocol (TCP) | 5432 | persistence |
| API → RabbitMQ | AMQP 0-9-1 (TCP) | 5672 | domain events |

All container ports are published on `127.0.0.1` only in development; on a server see [deployment.md](deployment.md).

## UDP vs. TCP for vehicle links

* **UDP** has no connection and no delivery guarantee. That suits telemetry: a late position is worthless, a lost one
  is replaced 100 ms later. "Connected" therefore cannot come from the transport; it comes from **heartbeats**
  (1 Hz, link lost after 3 s of silence).
* **TCP** retransmits lost data. Over a lossy radio that causes head-of-line blocking: fresh telemetry waits behind
  retransmissions. It is used when the other side only offers TCP (some SITL setups, serial bridges).

## UDP listen mode (how the GCS receives)

The GCS binds a local port (default 14550, like QGroundControl's "udpin"). The vehicle or SITL sends there; the GCS
replies to whatever address the vehicle last sent from. Consequences:

* The GCS cannot send anything before the vehicle has spoken (it does not know the address yet).
* Several vehicles can share one port (Phase 12, below).
* On Windows, an ICMP "port unreachable" from an earlier send would surface as an exception on the next receive;
  the transport disables `SIO_UDP_CONNRESET` so the listening socket survives the vehicle side restarting.

## Failure handling

| Event | Detection | Reaction |
|---|---|---|
| Vehicle stops sending | no HEARTBEAT for `HeartbeatTimeout` | `Reconnecting`, exponential backoff with jitter |
| Vehicle never answers | no HEARTBEAT within `ConnectTimeout` | `Faulted` with a reason; operator retries |
| Retries exhausted | `MaxReconnectAttempts` failed sessions | `Faulted`; no endless loop |
| Port already in use | socket bind fails when opening | treated as a failed attempt, reason reported |
| Corrupted bytes | CRC mismatch | frame dropped, `CrcErrors` counter |
| Lost frames | gaps in per-sender sequence numbers | `FramesLost`, `PacketLossRatio` in link status |
| Many vehicles drop at once | — | jitter spreads their retries over time |

Frames with a message id the GCS does not decode are skipped, but their sequence numbers are still tracked, so they
do not count as lost. A real PX4 streams about 35 message types and the GCS decodes a handful.

## Several vehicles on one UDP port (Phase 12)

PX4 multi-vehicle SITL and many radios send every vehicle to the same GCS port. The GCS keeps **one socket per local
endpoint** (`UdpEndpointHub`), shared by all vehicle links on it, and routes each datagram by the **system id** of its
first frame (header byte 5 in MAVLink 2, byte 3 in MAVLink 1). Replies go to the address that system id last sent from.

```
UAV-1 (sysid 1) ─┐                          ┌─► link of UAV-1
UAV-2 (sysid 2) ─┼─► 0.0.0.0:14550 ─ hub ───┼─► link of UAV-2
sysid 7 (new)   ─┘                          └─► nobody: listed as "heard, not registered"
```

* Register each vehicle with transport `Udp`, the same host and port, and its own system id. Two vehicles with the
  same system id on one port are refused (the second link fails with the reason): MAVLink cannot tell them apart.
* A system nobody registered is counted and shown in the topology, so a vehicle that was switched on but not yet
  added is visible.
* A telemetry radio reports `RADIO_STATUS` under its own system id (SiK: 51). Such frames from the same address as a
  vehicle go to that vehicle's link.
* The socket is bound when the first link opens and closed when the last one leaves.

## Link quality (Phase 12)

`GET /api/v1/vehicles/{id}/connection` returns, besides the totals since the link started:

| Field | Meaning | Source |
|---|---|---|
| `recentPacketLossRatio` | lost / (arrived + lost) over the last 10 s | sequence gaps, ten 1-second buckets |
| `messagesPerSecond` | frames per second over the last 10 s | same window |
| `roundTripMilliseconds` | smoothed round trip (EWMA, α = 0.3); null until answered | TIMESYNC: the GCS sends its clock at 1 Hz, the autopilot echoes it |
| `lastFrameAt` | when the last frame arrived | |
| `radio` | RSSI, remote RSSI, noise, errors, corrected packets, TX buffer | RADIO_STATUS from a telemetry radio |
| `grade` | Lost, Poor, Fair, Good | `LinkQualityRules` |

| Grade | Rule (first match wins) |
|---|---|
| Lost | not Connected, or no frame for more than 3 s |
| Poor | recent loss ≥ 15 % or round trip ≥ 1000 ms |
| Fair | recent loss ≥ 3 % or round trip ≥ 300 ms |
| Good | otherwise |

Clients receive `LinkQualityUpdated` on `/hubs/vehicles` every 2 s per active link; the desktop shows it as a coloured
line under each vehicle. The same values are OpenTelemetry gauges (`gcs.link.*`, [observability.md](observability.md)).
PX4 answers TIMESYNC out of the box; the PX4 SITL flight test checks the round trip and the Good grade.

## Network topology (Phase 12)

`GET /api/v1/network/topology` (any signed-in role) returns the GCS's endpoints, the vehicles behind each with their
link quality and source address, systems heard but not registered, and radio nodes:

```json
{
  "endpoints": [{
    "id": "udp://0.0.0.0:14550", "transport": "Udp",
    "vehicles": [{ "callsign": "UAV-1", "systemId": 1, "state": "Connected", "remote": "10.0.0.21:14580",
                   "quality": { "grade": "Good", "roundTripMilliseconds": 18, "recentPacketLossRatio": 0 } }],
    "unregisteredSystems": [{ "systemId": 7, "remote": "10.0.0.27:14580", "datagrams": 312 }]
  }],
  "radios": [{ "id": "radio-…", "kind": "mavlink-radio", "status": { "rssi": 182, "remoteRssi": 176 },
               "neighbours": [{ "nodeId": "radio-…-air", "rssi": 182 }] }]
}
```

Radios come from `IRadioNetworkProvider` implementations. Today there is one (RADIO_STATUS on MAVLink links). A mesh
(MANET) radio would add a provider that asks the radio itself (SNMP or its REST API) for its nodes and neighbours; the
GCS observes the mesh, it does not route over it ([ADR-019](adr/ADR-019-advanced-networking.md)).
