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

All container ports are published on `127.0.0.1` only.

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
* One port per vehicle in Phase 3. Several vehicles on one port, demultiplexed by system id, is Phase 12 work.
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

Link quality (`framesReceived`, `framesLost`, `packetLossRatio`, `crcErrors`) is returned by
`GET /api/v1/vehicles/{id}/connection` and is the basis for the connection quality indicators planned in Phase 12.
