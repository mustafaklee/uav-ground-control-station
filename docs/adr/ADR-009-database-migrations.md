# ADR-009: Database migrations with EF Core, applied by a separate migrator in deployments

* Status: Accepted
* Date: 2026-10-07

## Context

The database schema changes as features are added (vehicles now, missions and audit logs later). Schema changes must
be versioned, reviewable in pull requests, reproducible on every environment, and must never run twice at the same
time against the same database.

## Decision

* Schema changes are written as **EF Core migrations** in `src/Gcs.Persistence/Migrations`, generated with the
  repository-local `dotnet-ef` tool (`dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/Gcs.Persistence --startup-project src/Gcs.Persistence`).
  Generated SQL is reviewed in the pull request like any other code.
* **Development and integration tests:** the API applies pending migrations on startup when
  `Persistence:ApplyMigrationsOnStartup=true` (set in `appsettings.Development.json` and by the test factory).
* **Docker and production:** the API **never** changes the schema. A one-shot `gcs-migrator` container runs an EF Core
  *migration bundle* (a self-contained executable with every migration) and exits. The API container depends on it
  with `service_completed_successfully`, so the API starts only on an up-to-date schema.
* Migrations must be backwards compatible with the previous API version where possible (add columns as nullable,
  backfill, then tighten), so a rollback of the API does not require a rollback of the schema.

## Alternatives considered

* **Migrate on API startup everywhere:** simplest, but several API replicas starting together race on the schema,
  and the API's database user would need DDL rights (create/drop tables).
* **Hand-written SQL scripts (Flyway, DbUp):** full control over SQL, but duplicates the model already described by
  EF Core and loses the model snapshot that detects drift.
* **`dotnet ef database update` from CI:** requires the SDK and network access from CI to every database.

## Consequences

* Schema changes are explicit, ordered and reviewable.
* Deployments have one extra step (the migrator), already wired into `docker-compose.yml` and CI.
* Follow-up (Phase 8/11): give the migrator and the API **separate database users**, so the API runs without DDL rights.
