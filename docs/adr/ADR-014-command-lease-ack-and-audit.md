# ADR-014: Vehicle commands: lease, acknowledgement and audit

* Status: Accepted
* Date: 2026-10-07
* Phase: 7

## Context

Phase 7 lets operators command vehicles: ARM, DISARM, TAKEOFF, LAND, RTL and SET_MODE. A command does something
physical and often irreversible. The design has to answer the questions the project brief asks for every feature:

1. What happens if two operators control the same vehicle?
2. What happens if a command times out?
3. What happens if the same command is sent twice?
4. Who sent which command, and what came of it? (audit)
5. Which commands are dangerous enough to need an explicit confirmation?

Authentication arrives in Phase 8, so there are no users or roles yet.

## Decision

### 1. One operator per vehicle: an expiring command lease

A vehicle accepts commands only from the operator who holds its **command lease**
(`POST/DELETE /api/v1/vehicles/{id}/command-lease`).

* A free or expired lease can be taken by anyone. The holder can renew it. Anyone else gets `409 command.lease_held`
  with the holder's name and the expiry time.
* The lease lasts 60 s (`Commands:LeaseSeconds`). Every command renews it, and the desktop renews it every 20 s while
  it runs. A GCS that crashes or loses its network frees the vehicle within a minute. No one has to "unlock" it by hand.
* Only the holder can release it. A forced takeover by a supervisor needs roles and comes with Phase 8.
* Lease changes are pushed over SignalR (`CommandLeaseChanged`) so every screen shows who is in control.

**Leases are kept in memory, not in PostgreSQL.** The radio links themselves are in memory and owned by one API
process. A lease on a vehicle whose link died with that process means nothing. After a restart every operator takes
control again, which is the safe default. When the API runs as several instances (Phase 11/12), the lease store moves
to a shared store (a PostgreSQL row with `SELECT ... FOR UPDATE`, or Redis `SET NX PX`). `ICommandLeaseStore` is the
seam for that change. This is one of the Redis triggers listed in ADR-011.

Until Phase 8 the operator is named by the `X-Operator` header (letters, digits, `. _ @ -`, at most 64 characters).
This is **identification, not authentication**: anyone can type any name. It exists so the lease and audit logic are
complete and tested now. Phase 8 replaces the header with the authenticated user and adds role checks
("critical commands require appropriate authorization"). Handlers and tests do not change.

### 2. COMMAND_LONG, COMMAND_ACK, timeout and retry

Commands use `COMMAND_LONG` and wait for `COMMAND_ACK` (`CommandExchange`, following the MAVLink command protocol):

* Wait 1.5 s for the ACK (`Mavlink:CommandAckTimeoutMilliseconds`), then resend, up to 3 times (`CommandMaxRetries`).
  Each resend increments the `confirmation` field, so the autopilot can tell a retransmission from a new command.
* `ACCEPTED` → success. Any other result (`DENIED`, `FAILED`, `TEMPORARILY_REJECTED`, ...) is **final and not
  retried**. The vehicle answered, and asking again does not change its mind.
* `IN_PROGRESS` → keep waiting (up to 10 s) without resending. A resend would restart the operation.
* No answer after all attempts → **TimedOut**, HTTP `504` with `command.timed_out`. A timeout means "unknown", not
  "failed". The command may have been executed and only the ACK lost. The message tells the operator to check the
  vehicle's state (telemetry) before trying again, and the audit log records `TimedOut`, not `Rejected`.

Autopilot differences stay in `CommandMapper`. For example, PX4 reads the TAKEOFF altitude (`param7`) as metres above
mean sea level, while ArduPilot reads it as metres above home. The operator always enters metres above home. For PX4
the GCS adds the home altitude, which it derives from `GLOBAL_POSITION_INT` (MSL altitude minus relative altitude).
Until position telemetry has arrived, a PX4 takeoff is refused (`command.unsupported`) instead of guessed.

### 3. The same command twice: refused, not queued

A `COMMAND_ACK` carries only the command number (`MAV_CMD`), no request id. Two identical commands in flight could not
be told apart, so the link refuses a second command with the same `MAV_CMD` while the first is waiting
(`409 command.in_flight`). ARM and DISARM share `MAV_CMD_COMPONENT_ARM_DISARM`, so they also exclude each other.
**Different** commands may overlap. An RTL is never blocked by a mode change still waiting for its answer.

We refuse instead of queueing. A queued duplicate would run seconds later, when the situation may have changed (for
example, a second TAKEOFF after the vehicle has already started landing). The desktop also disables a button while its
command runs, but the server rule holds for every client.

### 4. Audit log: write-ahead, append-only, in PostgreSQL

Every command attempt is a row in `gcs.command_audit`: vehicle, callsign (copied, so a later rename does not rewrite
history), operator, command, parameters, source, outcome, detail, attempts, requested/completed time.

* **Write-ahead.** The row is saved as `Pending` **before** the command is sent and completed afterwards. If the
  database is down, the command is not sent at all (no command without a record). If the API crashes mid-command, the
  `Pending` row shows that an attempt was made.
* **Refused attempts are recorded too** (no lease, not connected, duplicate). They are exactly what an investigation
  looks for.
* **Append-only, enforced by the database.** A trigger allows one update (`Pending` → outcome) and rejects every other
  update and every delete. A bug or a hand-written SQL statement cannot rewrite the log.
* Once a command is on its way, the HTTP request's cancellation is ignored, so a closed client cannot leave a row
  without an outcome. The exchange is bounded by its own timeouts.
* Read with `GET /api/v1/vehicles/{id}/commands` (paged, newest first).

The audit log is not sent through the RabbitMQ outbox in this phase. Nothing consumes it yet. When Phase 9
(observability) or an external system needs command events, an outbox event can be added from the same handler.

### 5. Confirmation for critical commands

ARM, DISARM, TAKEOFF and SET_MODE require `"confirm": true` in the request (`400 command.confirmation_required`
otherwise). The desktop shows a dialog in which the confirm button is not the default button, so a reflexive Enter does
nothing. **LAND and RTL need no confirmation.** They are the operator's reaction when something goes wrong and must
stay one click away. The server enforces the flag as well, so a script or another client cannot skip it by accident.

## Consequences

* Two operators can no longer send conflicting commands to one vehicle. The second one is told who is in control.
* Every outcome is explicit and audited: Accepted, Rejected (with the vehicle's reason), TimedOut (state unknown), or
  Refused (with the GCS's reason).
* A restart drops all leases (operators take control again) but no audit history.
* Leases must move to a shared store before the API runs as more than one instance.
* `X-Operator` is not security. Until Phase 8, anyone who can reach the API can claim any name.

## Alternatives considered

* **No lease, last command wins.** Simple, but two operators could fight over one vehicle. Rejected for safety.
* **Lease without expiry, released by hand.** A crashed GCS would lock a vehicle until someone noticed. Rejected.
* **Lease in PostgreSQL now.** Correct across instances, but it adds a write per command and per renewal, and it gives
  no benefit while links are per process. Deferred until there are several instances.
* **Queue duplicate commands.** Rejected (see 3): a stale command executed later is worse than a clear refusal.
* **Audit after the command only.** One write fewer, but a crash between sending and saving would leave no trace of a
  command that may have been executed. Rejected.
* **COMMAND_INT for TAKEOFF/LAND.** It carries exact positions, but we command "here", so COMMAND_LONG is enough and
  both autopilots support it for all six commands.
