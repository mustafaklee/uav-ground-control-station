# ADR-019: Advanced networking: link quality, several vehicles on one port, topology, radio abstraction

* Status: Accepted
* Date: 2026-10-09
* Phase: 12

## Context

The brief lists Phase 12 as topics to evaluate rather than build in full: multiple vehicles, network topology, modem
status, connection quality, packet loss, latency, a MANET abstraction, network monitoring, SNMP and later NetFlow.

What exists after Phase 11:

* Packet loss counted from MAVLink sequence numbers, **cumulative since the link started**. After an hour of flight a
  bad last minute barely moves the number, so it cannot drive an operator indicator.
* No latency measurement at all.
* One UDP socket per vehicle, bound exclusively. A second vehicle sending to the same port (the normal case for
  several PX4 instances, or a swarm behind one radio) cannot be connected: its link fails with "port in use".
* Link status reaches clients only when the **state** changes (Connected, Reconnecting...), never when quality changes.

Questions to decide:

1. What do we measure, and how do we turn numbers into something an operator reads at a glance?
2. How do several vehicles share one UDP port?
3. What is "topology" for this system, and where do radios and a future MANET fit?
4. How do link metrics reach operators and the monitoring stack?
5. What is deliberately left out (SNMP, NetFlow, mesh routing)?

## Decision

### 1. Link quality: recent loss, round-trip time, message rate, radio status, and one grade

| Metric | Source | Why |
|---|---|---|
| **Recent packet loss** | Sequence gaps over a sliding **10-second window** (ten 1-second buckets) | Reacts within seconds; cumulative loss stays for totals |
| **Round-trip time (RTT)** | MAVLink **TIMESYNC** (111): the GCS sends `ts1` = its clock, the autopilot echoes it; RTT = now − `ts1`. Sent at 1 Hz, smoothed (EWMA, α = 0.3) | Supported by PX4 and ArduPilot without any setup; no clock synchronisation needed because only our own clock is compared |
| **Message rate** | Frames per second over the same window | A falling rate is the first sign of a saturated or failing radio |
| **Radio status** | **RADIO_STATUS** (109), sent by SiK and other telemetry radios: local/remote RSSI, noise, receive errors, corrected packets, TX buffer | The "modem status" of the brief, for links that have a MAVLink radio |

A pure domain rule, `LinkQualityGrade`, turns them into **Good / Fair / Poor / Lost**:

| Grade | Rule (first match wins) |
|---|---|
| Lost | link not Connected, or no frame for more than 3 s |
| Poor | recent loss ≥ 15 %, or RTT ≥ 1000 ms |
| Fair | recent loss ≥ 3 %, or RTT ≥ 300 ms |
| Good | otherwise |

The thresholds follow common ground-station practice (QGroundControl turns its link indicator yellow at a few percent
loss) and are kept in one place with unit tests, so they can be tuned after field use.

### 2. Several vehicles on one UDP port: a shared endpoint, demultiplexed by system id

A process-wide `UdpEndpointHub` owns one socket per local endpoint (for example `0.0.0.0:14550`), reference-counted:
bound when the first vehicle link opens it, closed when the last one closes. Each datagram is routed by the **system id
of its first frame** (byte 5 of a MAVLink 2 header, byte 3 of MAVLink 1) to the link that claimed that system id, and
replies go to the address that system id last sent from. Each vehicle link still sees an ordinary
`IMavlinkTransport`, so `MavlinkConnection` does not change.

* Two vehicles registered with the **same system id on the same port** cannot be told apart: the second link fails
  with a clear reason instead of stealing the first one's traffic.
* Datagrams from system ids nobody claimed are counted per endpoint: the topology shows "heard but not registered"
  systems, which is how an operator discovers a vehicle that was switched on but not yet added.
* Rejected alternative: one port per vehicle (today). It forces a different port into every vehicle's configuration
  and does not match how PX4 multi-vehicle SITL and many radios work (everyone sends to 14550).

### 3. Topology: GCS → link endpoints → vehicles, with radios behind an abstraction

`GET /api/v1/network/topology` returns a small graph:

```
GCS ── endpoint udp://0.0.0.0:14550 ──┬── vehicle UAV-1 (sysid 1)   Good, 12 ms, 0 %
       (radio: RSSI 180/175)          ├── vehicle UAV-2 (sysid 2)   Fair, 340 ms, 4 %
                                      └── sysid 7 heard, not registered
GCS ── endpoint tcp://10.0.0.5:5760 ──── vehicle UAV-3 (sysid 3)   ...
```

Radios are behind `IRadioNetworkProvider` (Application layer): it reports radio **nodes** (id, endpoint, signal
metrics, and for a mesh its neighbours with link metrics). The first implementation reads RADIO_STATUS from MAVLink.
A MANET radio (for example a mesh radio with its own management API) would be a second implementation that queries the
radio over SNMP or its REST API and reports neighbours; the topology endpoint merges whatever providers are registered.
This is the "MANET abstraction": the GCS does not route over the mesh (the radios do that); it **observes** it.

### 4. Push and monitoring

* A background service broadcasts `LinkQualityUpdated` on the vehicles hub every 2 seconds for each active link
  (state changes keep their immediate `LinkStatusChanged`). Two seconds is fast enough for an indicator and cheap:
  one small message per vehicle.
* OpenTelemetry gauges per vehicle: `gcs.link.rtt` (ms), `gcs.link.packet_loss` (recent ratio),
  `gcs.link.message_rate`, and `gcs.link.radio.rssi` per radio. They appear in the Aspire dashboard and any OTLP backend,
  which covers "network monitoring" for the GCS's own links.
* The desktop vehicle list shows the grade as a coloured dot with RTT and loss.

### 5. Deferred

| Topic | Why not now | When |
|---|---|---|
| SNMP polling of radios and switches | Needs a target radio model and its MIB; the provider interface is the hook | When a MANET radio is chosen |
| NetFlow / IPFIX | Flow export is a router/switch feature; the GCS would only consume it in a separate network-monitoring stack | Outside the GCS |
| MAVLink routing between links, mesh routing | The radios route; the GCS forwarding packets between vehicles would duplicate that and add failure modes | Not planned |
| MAVLink 2 signing, restricting MAVLink to the vehicles' network (`DOCKER-USER`) | Needs key distribution to vehicles; separate security work | Own phase |
| Serial links (USB radios) | No serial transport yet | With the first USB radio |

## Consequences

* Operators see link health per vehicle and per radio, updated every two seconds, and can tell "radio is weak"
  (RSSI) from "network is congested" (rate, RTT) from "vehicle is gone" (Lost).
* Any number of vehicles can share one UDP port; each still needs a unique system id, which is already MAVLink's rule.
* TIMESYNC adds one 26-byte message per second per vehicle in each direction, negligible even on a 57.6 kbit/s radio.
* The grade thresholds are a starting point, to be tuned with field data.
