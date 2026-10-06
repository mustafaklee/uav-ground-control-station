# ADR-002: Avalonia UI for the operator desktop client

* Status: Accepted
* Date: 2026-10-06

## Context

Operators need a responsive desktop application with a map, instruments and mission planner that runs on Windows and
Linux workstations, including offline field deployments. The team works in C#.

## Decision

Use **Avalonia UI** with the **MVVM** pattern (CommunityToolkit.Mvvm). The client talks to the backend only through
REST and SignalR using types from `Gcs.Contracts`. It never references Domain, Application or MAVLink code
(enforced by architecture tests).

## Alternatives considered

* **WPF / WinUI**: mature, but Windows only.
* **.NET MAUI**: no desktop Linux support.
* **Web UI (Blazor/React)**: easy distribution, but weaker for offline field use, direct hardware access (joysticks, serial) and multi-monitor setups.
* **Qt/QML**: used by QGroundControl; would split the codebase into two languages.

## Consequences

* XAML skills transfer from WPF; one UI codebase for Windows and Linux.
* The map control and tile licensing are evaluated separately (Phase 5/6 open question: Mapsui or alternatives, offline tiles).
* Smaller ecosystem than WPF; some controls have to be built or sourced.
