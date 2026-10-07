# ADR-010: Own MAVLink v2 codec, verified against pymavlink (supersedes the library choice in ADR-007)

* Status: Accepted
* Date: 2026-10-07
* Supersedes: the "use Asv.Mavlink" part of [ADR-007](ADR-007-mavlink-abstraction.md). The abstraction strategy of ADR-007 stays.

## Context

ADR-007 chose Asv.Mavlink for message definitions, framing and parsing. Before writing Phase 3 we inspected the package:

* `Asv.Mavlink` 4.3.1 is a 3.8 MB assembly and pulls in `Asv.IO`, `Asv.Cfg`, `Asv.Common`, `Asv.Store`,
  `System.IO.Abstractions` and `ZLogger`. It is a complete device framework (its own transports, configuration,
  storage, logging and reactive model), not just a codec.
* Our architecture already owns those concerns (transports behind `IMavlinkTransport`, Serilog, DI, options).
  Adopting the library would mean either two parallel infrastructures or using a small corner of a large dependency.
* Phases 3–7 need about ten messages (HEARTBEAT, SYS_STATUS, GPS_RAW_INT, ATTITUDE, GLOBAL_POSITION_INT, VFR_HUD,
  COMMAND_LONG, COMMAND_ACK, later the MISSION_* set). MAVLink v2 framing is small and fully specified:
  10-byte header, little-endian payload sorted by field size, trailing-zero truncation, CRC-16/MCRF4XX seeded with a
  per-message CRC_EXTRA, optional 13-byte signature.

## Decision

* Implement a focused MAVLink v2 codec in `Gcs.Mavlink.Protocol`: frame parser (stream safe, resynchronizes after
  garbage, accepts v1 and v2 frames, validates CRC, skips signatures), frame writer (v2 with payload truncation) and
  hand-written message types for the messages we use.
* **Correctness is proven against the reference implementation.** `scripts/generate-mavlink-golden.py` uses pymavlink
  (maintained by the MAVLink project) to produce reference frames, committed as `GoldenFrames.g.cs`. Unit tests require
  our encoder to produce exactly those bytes and our decoder to read them back to the original values.
* Phase 10 adds the final check: a real PX4 SITL instance.
* Revisit when the message count grows beyond what is comfortable to maintain by hand (for example MAVLink FTP or
  parameter protocol): then generate message types from the official XML definitions at build time.

## Alternatives considered

* **Asv.Mavlink** (ADR-007): MIT, maintained, complete; rejected for weight and overlapping infrastructure as described above.
* **Generate all of `common.xml`** now: hundreds of unused types; worth it only when we need many more messages.
* **MAVSDK sidecar:** hides the protocol behind gRPC and adds a process to deploy.

## Consequences

* No third-party dependency on the vehicle link; the hot path allocates little and is fully under our control.
* We own correctness of each message definition. Mitigated by golden tests against pymavlink for every message.
* Adding a message means: add a record with `Read`/`Write`, register it, add a pymavlink case to the generator.
* Learning value: the team understands the protocol at the byte level, which matters when debugging radio links.
