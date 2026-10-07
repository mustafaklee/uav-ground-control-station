# ADR-016: Observability with OpenTelemetry

* Status: Accepted
* Date: 2026-10-07
* Phase: 9

## Context

When an operator reports "I pressed DISARM and nothing happened", we must be able to answer within minutes. Did the
request arrive? Who sent it? Did the audit row get written? Did the COMMAND_LONG leave? How many retries were there,
and what did the vehicle answer? The brief asks for exactly that chain: request → application → database → message
broker → MAVLink. It also asks for Serilog, OpenTelemetry, metrics, tracing and health monitoring.

Phases 1–8 gave us structured Serilog logs with a correlation id and liveness/readiness checks. Logs alone cannot
show timing, nesting or what happened on a background thread seconds later.

## Decision

### 1. OpenTelemetry for traces and metrics; Serilog stays the logging API

* `OpenTelemetry.Extensions.Hosting` with the ASP.NET Core, HttpClient, Npgsql and runtime instrumentations.
* Our own code uses only `System.Diagnostics` (`ActivitySource "Gcs"`, `Meter "Gcs"` via `IMeterFactory`), never the
  OpenTelemetry API. Domain and application layers stay vendor-neutral. The host decides where the data goes.
* Serilog keeps doing logging. Every log event carries `TraceId`/`SpanId` automatically (Serilog 4), and the console
  template prints the trace id next to the correlation id. With an OTLP endpoint, logs are also exported over OTLP, so
  the dashboard shows a span's log lines next to it.
* **One switch:** `Observability:OtlpEndpoint`. Empty means instrumented but not exported (tests, `dotnet run` without
  a collector). Set means all three signals go over OTLP/gRPC.

### 2. Custom spans where the framework cannot see

| Span | Kind | Where | Attributes |
|---|---|---|---|
| `vehicle.command {Type}` | Client | MAVLink connection | vehicle id, command, outcome, attempts; one event per `COMMAND_LONG` transmission (`mavlink.confirmation`); error status unless accepted |
| `mission.upload` / `mission.download` | Client | MAVLink connection | vehicle id; error status with the reason |
| `{EventType} publish` | Producer | RabbitMQ publisher | messaging system, exchange, routing key, message id |

The HTTP span is tagged with `gcs.correlation_id`, so the id from an error message finds its trace.

### 3. Trace context through the outbox

An event is published seconds after the request that raised it, on a background thread, where `Activity.Current` is
empty. So the outbox row stores the W3C `traceparent` of the request (`outbox_messages.trace_parent`). The publisher
starts its span as a child of it and puts its own `traceparent` into the AMQP message headers. Any consumer that reads
the header joins the same trace. A vehicle registration therefore shows as one trace: HTTP request → INSERTs →
(later) publish to RabbitMQ.

### 4. Sampling: drop background noise, keep everything else

The outbox poll (every second) and telemetry history batches run database queries with no parent span. Each would
become its own one-span trace, thousands per hour, burying the real ones. A parent-based sampler with a custom root
rule **drops root spans of kind Client** (a DB or HTTP call with nothing around it) and keeps everything else. Work
worth seeing in the background starts its own parent span first (the publish span), so its children are kept. Health
probes are filtered out of request tracing. Everything else is sampled at 100%: the traffic of a GCS is small, and an
unsampled incident is useless. Ratio sampling can be added if volume ever needs it.

### 5. Metrics

| Metric | Type | Attributes |
|---|---|---|
| `gcs.commands` | counter | `gcs.command`, `gcs.command.outcome` |
| `gcs.command.duration` | histogram (s) | same |
| `gcs.mission.transfers` | counter | `direction`, `result` |
| `gcs.mavlink.frames` | counter | `gcs.vehicle.id` |
| `gcs.link.state_changes` | counter | `gcs.vehicle.id`, `state` |
| `gcs.links` | gauge | `state` |
| `gcs.auth.logins` | counter | `result` |
| plus ASP.NET Core (request duration by route and status), HttpClient, Npgsql, .NET runtime (GC, thread pool) | | |

Vehicle id is an attribute on per-vehicle counters. The fleet size is small (tens), so the cardinality is acceptable
and per-vehicle frame rates are the most useful link-quality signal.

### 6. Health monitoring: three levels

| Endpoint | Checks | Who | Meaning |
|---|---|---|---|
| `/health/live` | none | anonymous | process is up (restart if not) |
| `/health/ready` | PostgreSQL, RabbitMQ | anonymous | send traffic here |
| `/health/details` | ready checks + vehicle links, outbox backlog, telemetry history | `read` permission | what is wrong operationally, with data |

The monitoring checks report **Degraded**, never take the instance out of rotation, and name what is affected: which
vehicle is faulted, how many events wait and for how long, how many history samples were dropped. Only an outbox
backlog older than 10 minutes is Unhealthy (`503`), because consumers are then missing events. The details describe
the fleet, so they require sign-in.

### 7. Local backend: the Aspire dashboard

`docker compose` runs `mcr.microsoft.com/dotnet/aspire-dashboard`, a single container that receives OTLP and shows
traces, metrics and structured logs (UI on `127.0.0.1:18888`, OTLP gRPC on `127.0.0.1:4317` for a host-run API). It
needs no configuration and fits a developer laptop. It runs with anonymous access, which is acceptable only because it
is bound to localhost and keeps data in memory.

For production (Phase 11), the API keeps speaking OTLP to an OpenTelemetry Collector, which forwards to durable
backends (for example Tempo/Jaeger for traces, Prometheus/Mimir for metrics, Loki/Seq for logs) with retention and
authentication. No application change is needed.

## Consequences

* Any operator action can be followed from the HTTP request to the vehicle's answer, and from a registration to the
  broker. The integration tests prove the chain by sending their own `traceparent`.
* One new column (`outbox_messages.trace_parent`) and a handful of package references in the API only.
* Background database work is not traced unless it belongs to a traced operation. That is deliberate.
* The local dashboard is not a production monitoring system: no retention, no alerting.

## Alternatives considered

* **Logs only (Serilog + Seq).** Good search, but no timing and no parent/child structure. Following the outbox hop
  would mean manual id juggling. Rejected as the only signal. Seq can still be a log backend behind the collector.
* **Vendor SDKs (Application Insights, Datadog) directly.** Lock-in at the code level. OTLP keeps the backend
  replaceable.
* **Jaeger + Prometheus + Grafana in compose now.** Closer to production, but three containers and configuration for a
  developer laptop. Deferred to deployment.
* **EF Core instrumentation.** Still in beta. Npgsql's own instrumentation already gives one span per SQL command
  with the statement.
