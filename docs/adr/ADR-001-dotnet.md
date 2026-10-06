# ADR-001: .NET 10 and C# for the backend and tools

* Status: Accepted
* Date: 2026-10-06

## Context

The GCS needs a long-lived backend that handles network I/O (UDP/TCP/serial MAVLink), real-time push to clients,
a relational database and a broker, and that runs on both Windows and Linux. The desktop client should ideally share
language, tooling and DTOs with the backend.

## Decision

Use **C# on .NET 10 (LTS, supported until November 2028)** with ASP.NET Core for the backend, the simulator and the
desktop client. The SDK version is pinned in `global.json`; package versions are pinned centrally in
`Directory.Packages.props`.

## Alternatives considered

* **Java / Spring Boot**: comparable maturity and defence-sector use; no first-class cross-platform XAML desktop story, and two languages if the UI is .NET.
* **Go**: excellent for network services, weaker for rich desktop UI and ORM-heavy domain code.
* **C++ / Qt** (the QGroundControl stack): best raw performance, much higher cost for web APIs, security hardening and testing.
* **Python** (MAVSDK/pymavlink): fastest prototyping, but the GIL, typing and packaging make it a poor fit for a long-running multi-vehicle server.

## Consequences

* One language and one toolchain end to end; contracts are shared between API and desktop client.
* Nullable reference types, analyzers and warnings-as-errors are enforced in `Directory.Build.props`.
* Good job-market alignment (ASP.NET Core, EF Core) and strong Linux container support.
* MAVLink support relies on a community library (see ADR-007) rather than an official vendor SDK.
