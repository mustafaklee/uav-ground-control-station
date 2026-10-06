# ADR-008: Redis only when a concrete need exists (evaluated in Phase 4)

* Status: Accepted
* Date: 2026-10-06

## Context

Redis is often added by default for caching. In this system the obvious candidate is the **latest vehicle state**
(position, attitude, battery, mode), which changes many times per second and is read by every connected client.

## Decision

Do **not** use Redis in Phases 1 to 3. The container is defined in `docker-compose.yml` so the environment is ready,
but no code depends on it. In Phase 4 we compare it with an in-process latest-state store:

* With a **single API instance**, an in-memory concurrent dictionary is faster and adds no failure mode.
* Redis becomes justified when state must survive an API restart, be shared by **several API instances**, or act as
  the **SignalR backplane**.

## Alternatives considered

* **Redis from the start**: an unused moving part that can fail and must be secured.
* **Valkey** (BSD-licensed Redis fork): protocol compatible; compared with Redis 8 licensing if Redis is adopted.

## Consequences

* Fewer dependencies until there is a measured reason.
* The latest-state store is an Application port, so moving from in-memory to Redis is an adapter change only.
