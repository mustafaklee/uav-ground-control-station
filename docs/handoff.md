# Handoff (2026-10-09)

Where the work stopped and what comes next. Update or remove this file when work resumes.

## State of the branches

| Branch | PR | State |
|---|---|---|
| `main` | | Phases 1–11 (#10 and #11 merged 2026-10-08) |
| `feature/phase-12-advanced-networking` | → `main` | Rebased onto `main`, pushed, PR open |

Stale branches that can be deleted on GitHub: `feature/phase-9-observability`, `feature/phase-9-observability-main`,
`feature/phase-10-px4-sitl`, `feature/phase-11-deployment`.

## Phase 12 (Advanced networking): done

* ADR-019. MAVLink RADIO_STATUS (109) and TIMESYNC (111), golden-tested against pymavlink.
* `LinkQualityMonitor` (10 s window, EWMA round trip, radio), `LinkQualityRules` (Good/Fair/Poor/Lost) in the domain.
* `UdpEndpointHub`: several vehicles on one UDP port, routed by system id; unregistered systems are listed.
* `GET /api/v1/network/topology`, `IRadioNetworkProvider` (first implementation: RADIO_STATUS).
* `LinkQualityUpdated` pushed every 2 s; gauges `gcs.link.rtt|packet_loss|message_rate|radio.rssi`; desktop quality line.
* The simulator answers TIMESYNC and can simulate a SiK radio (`Simulator__SimulateRadio`, on in dev compose).
* Real PX4 answers TIMESYNC: the SITL flight test checks the round trip and the Good grade.
* Docs: networking.md, mavlink.md, observability.md, README, `docs/learning/phase-12-rehberi.md`.
* Tests: 379 unit, 11 architecture, 131 integration (+1 opt-in SITL, which passed locally).

## Next steps

1. Drive the Phase 12 PR to green CI; Mustafa reviews and merges.
2. The twelve phases of the brief are complete. Anything beyond them waits for Mustafa's decision.

## Local machine notes

* `.env` (git-ignored) holds `JWT_SIGNING_KEY` and `GCS_ADMIN_PASSWORD`. The same values are in `dotnet user-secrets`
  for `src/Gcs.Api`.
* The dev stack is not running. Start it with `docker compose --profile sitl up -d --build --wait`.
* `deploy/test/verify-install.sh` tests the server install in a throwaway Ubuntu container (about 15 minutes).
