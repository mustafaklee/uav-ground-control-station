# ADR-013: Mission storage and validation

* Status: Accepted
* Date: 2026-10-07
* Phase: 6

## Context

A mission is an ordered list of items that is always read, edited, versioned and uploaded as a whole. Operators build
missions step by step, so a plan that is half done (no takeoff yet, no final RTL) is a normal state while editing, not
an error. A plan that reaches a vehicle must be complete, though. A vehicle that takes off with no terminal item, or
meets a takeoff in the middle of its route, is a safety problem.

Two questions:

1. Where do the items live?
2. Which rules reject a request, and which only describe a plan that is not ready yet?

## Decision

**Items are stored as JSONB in the mission row** (`missions.items`, EF Core owned collection mapped with `ToJson`).
The last upload result is stored the same way in `last_upload`.

* Items have no identity of their own. They are always loaded and saved with their mission. Ordering is the array
  order, so there is no sequence column to renumber on every insert or move.
* One row means one optimistic-concurrency version (`If-Match`) for the whole plan. Two operators editing the same
  mission cannot interleave item changes.
* We do not query inside items, such as "all missions passing a point". If that need appears, a generated column or a
  separate read model can be added without changing the aggregate.

**Validation has two levels:**

| Level | Examples | Effect |
|---|---|---|
| Field rules (per item, per mission) | altitude 2–500 m, coordinates in range, loiter needs hold time, name length, ≤ 500 items | `400` with per-item keys (`items[3]`); nothing is stored |
| Flyability rules (shape of the plan) | first is takeoff, last is land/RTL, nothing after land/RTL, at least one waypoint | stored; returned as `issues`, `isFlyable = false` |

Upload is the gate: `POST /missions/{id}/upload` refuses a mission with issues (`409 mission.not_flyable`) before any
MAVLink traffic.

Upload results are recorded on the mission without bumping its version. An upload does not change the plan, and it
must not make an operator's open editor stale.

## Consequences

* Drafts can be saved and shared. The UI shows the remaining issues on the rows they concern.
* The same `Mission.Validate()` runs for the API response and the upload gate, so they cannot disagree.
* The vehicle can still reject a mission (`MISSION_ACK` error). That result is stored in `lastUpload` and shown to the
  operator. Server-side validation reduces such rejections but does not replace the autopilot's own checks.
* Changing the item schema later needs a data migration of JSON documents rather than an `ALTER TABLE`. The item shape
  is small and versioned with the API, which we accept.

## Alternatives considered

* **`mission_items` table with a sequence column.** This is classic normalisation, but every reorder rewrites
  sequence numbers, concurrency needs a version on the parent anyway, and nothing queries items on their own.
* **Reject any unflyable mission on save.** It is simple, but it forces operators to build the whole plan before they
  can save it once, and it loses work when the client crashes.
* **Validate only in the client.** The API is also used by scripts and future clients, so the server must own the
  rules.
