# Telemetry

How vehicle telemetry flows from the MAVLink link to operator screens and to storage.

## Pipeline

```
MavlinkConnection (receive loop, per vehicle)
   │ TelemetryTranslator: wire units → degrees / metres / m/s
   ▼
TelemetryPipeline : ITelemetrySink                      ── never blocks the receive loop ──
   ├─► LatestTelemetryStore      one immutable snapshot per vehicle (memory)
   │      ├─► GET /api/v1/vehicles/{id}/telemetry       (REST, on demand)
   │      └─► TelemetryBroadcaster  every 200 ms, only changed vehicles
   │              └─► ILiveUpdatePublisher → SignalR /hubs/telemetry, group "vehicle:{id}"
   └─► TelemetryHistoryBuffer    1 sample / vehicle / second → bounded queue (drop oldest)
          └─► TelemetryHistoryWriter  batch of 500 or every 2 s → PostgreSQL gcs.telemetry_samples
                 └─► GET /api/v1/vehicles/{id}/telemetry/history
TelemetryRetentionService: hourly, deletes samples older than 30 days
```

Link state changes take a parallel path: `MavlinkConnection` → `IVehicleLinkEventSink` (bounded queue) →
`VehicleLinkEventDispatcher` → SignalR `/hubs/vehicles` and, for meaningful transitions, the outbox → RabbitMQ.

## Why each rate

| Stage | Rate | Reason |
|---|---|---|
| Vehicle → GCS | 10–50 Hz per message type | the autopilot's stream rates |
| Latest state | every message | cheap (in-memory), always current for REST and new subscribers |
| Push to clients | 5 Hz per vehicle | humans cannot read faster; bandwidth scales with subscribers × vehicles |
| History | 1 Hz per vehicle | enough for a flight track and charts; ~2.6 M rows per vehicle per month instead of ~130 M messages |

## Failure behaviour

| Failure | Effect |
|---|---|
| Slow or disconnected SignalR client | that push fails or waits; MAVLink reception is unaffected (separate loop) |
| PostgreSQL down | history samples queue in memory (default 10 000); when full, the oldest are dropped and counted; live telemetry continues |
| PostgreSQL back | the writer retries the pending batch and drains the queue |
| API restart | snapshots are rebuilt from the next messages within about a second; history up to the last flush is kept |

## Realtime API (SignalR)

| Hub | Client calls | Server sends |
|---|---|---|
| `/hubs/telemetry` | `SubscribeVehicle(id)`, `UnsubscribeVehicle(id)` | `TelemetryUpdated(TelemetryResponse)`; the current snapshot immediately on subscribe |
| `/hubs/vehicles` | — | `LinkStatusChanged(VehicleLinkResponse)` for every vehicle |

Names and DTOs live in `Gcs.Contracts.Realtime`, shared with the desktop client.

```csharp
var hub = new HubConnectionBuilder().WithUrl("http://localhost:8080/hubs/telemetry").WithAutomaticReconnect().Build();
hub.On<TelemetryResponse>(nameof(ITelemetryHubClient.TelemetryUpdated), t => Console.WriteLine(t.Position));
await hub.StartAsync();
await hub.InvokeAsync(TelemetryHubMethods.SubscribeVehicle, vehicleId);
```

## Configuration (`Telemetry:` section)

| Key | Default | Meaning |
|---|---|---|
| `BroadcastIntervalMilliseconds` | 200 | live push period |
| `HistorySampleIntervalMilliseconds` | 1000 | one stored sample per vehicle per period |
| `HistoryBufferCapacity` | 10000 | samples kept in memory while the database is unavailable |
| `HistoryFlushIntervalMilliseconds` / `HistoryBatchSize` | 2000 / 500 | write when either is reached |
| `HistoryRetentionDays` | 30 | 0 keeps history forever |

## Why not Redis

See [ADR-011](adr/ADR-011-redis-evaluation-phase-4.md): with one API instance, memory is faster and has no failure
mode. Redis becomes the right tool when there are several API instances (SignalR backplane, shared state) or separate
link workers.

## Scaling notes

* History is one narrow, append-only table indexed by `(vehicle_id, recorded_at)`. At larger scale: PostgreSQL native
  partitioning by month, or the TimescaleDB extension (no database change, see ADR-004).
* Batch inserts use EF Core's batching; Npgsql binary `COPY` is the next step if write throughput ever matters.
