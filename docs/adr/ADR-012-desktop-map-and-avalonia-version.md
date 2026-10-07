# ADR-012: Mapsui for the operator map, Avalonia 11.3, OpenStreetMap tiles now, offline tiles before field use

* Status: Accepted
* Date: 2026-10-07
* Refines: [ADR-002](ADR-002-avalonia.md)

## Context

Phase 5 needs a map in the Avalonia client showing the vehicle, its heading, its track and its home position, and in
Phase 6 waypoints, routes and geofences. The project brief asks to weigh licensing and offline use when choosing the
map provider. The client was scaffolded on Avalonia 12.

## Decision

1. **Map control: Mapsui 5.1** (MIT). Mature .NET map library with an Avalonia control, tile layers (via BruTile),
   memory layers for our own features, Web Mercator projection helpers and NetTopologySuite geometry support for
   routes and geofences.
2. **Avalonia 11.3.22 instead of 12.** Mapsui 5.1 is built and tested against Avalonia 11.3. Running a UI library
   against a major version it was not built for risks runtime failures (missing members) that only appear on screen.
   Avalonia 11.3 is the mature, widely deployed line, which fits the "well-tested tools" requirement. Upgrade when
   Mapsui ships Avalonia 12 support.
3. **Tiles: OpenStreetMap for development**, following the OSM tile usage policy: an identifying User-Agent, normal
   interactive use only, and the "© OpenStreetMap contributors" attribution always visible on the map.
4. **Before field use: offline tiles.** A GCS must work without internet. Planned (Phase 6/11): MBTiles packages for the
   operating area (BruTile MbTiles provider) or a self-hosted tile server on the GCS network. The map layer is created
   in one place (`VehicleMap`), so switching the tile source is a one-line change.

## Alternatives considered

* **Avalonia 12 with a hand-written tile map:** no version conflict and full control, but weeks of work on panning,
  zooming, tile caching and projections that Mapsui already provides.
* **Avalonia 12 with Mapsui anyway:** compiles (the dependency is ">= 11.3"), but untested at runtime. Rejected.
* **Commercial SDKs (ArcGIS Runtime, Google/Bing maps):** licence cost and terms, and online accounts conflict with
  offline field use.
* **WebView with Leaflet/OpenLayers:** proven web map stacks, but embedding a browser engine adds weight and a
  JavaScript bridge to a native client.

## Consequences

* The desktop client stays on Avalonia 11.3 until Mapsui supports 12; the backend is unaffected.
* OSM attribution is part of the UI and must not be removed.
* Offline tile support is a tracked requirement before any field deployment.
