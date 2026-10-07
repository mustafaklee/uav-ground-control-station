# Architecture Decision Records

Each ADR records one significant decision: its context, the options considered and the consequences.
New ADRs get the next number; superseded ADRs are kept and marked as superseded.

| ADR | Decision |
|---|---|
| [ADR-001](ADR-001-dotnet.md) | .NET 10 and C# for the backend and tools |
| [ADR-002](ADR-002-avalonia.md) | Avalonia UI for the operator desktop client |
| [ADR-003](ADR-003-modular-monolith.md) | Modular monolith with Clean Architecture |
| [ADR-004](ADR-004-postgresql.md) | PostgreSQL with EF Core as the system of record |
| [ADR-005](ADR-005-rabbitmq.md) | RabbitMQ for domain events only, via a transactional outbox |
| [ADR-006](ADR-006-signalr.md) | SignalR for real-time push to clients |
| [ADR-007](ADR-007-mavlink-abstraction.md) | MAVLink abstraction strategy (library choice superseded by ADR-010) |
| [ADR-008](ADR-008-redis-deferred.md) | Redis only when a concrete need exists (evaluated in Phase 4) |
| [ADR-009](ADR-009-database-migrations.md) | Database migrations with EF Core, applied by a separate migrator in deployments |
| [ADR-010](ADR-010-own-mavlink-codec.md) | Own MAVLink v2 codec, verified against pymavlink |
| [ADR-011](ADR-011-redis-evaluation-phase-4.md) | Redis evaluated in Phase 4 and not adopted yet; triggers for adopting it |
