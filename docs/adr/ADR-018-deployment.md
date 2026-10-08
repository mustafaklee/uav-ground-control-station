# ADR-018: Single-host deployment on Ubuntu 24.04 with Docker Compose, Nginx, UFW and a scripted install

* Status: Accepted
* Date: 2026-10-09
* Phase: 11

## Context

Until Phase 10 the backend ran only on developer machines (`docker-compose.yml`, ports on 127.0.0.1, plain HTTP).
Phase 11 puts it on an Ubuntu Server so operators on other machines can use it. A ground control station is a
safety-relevant system often run on closed networks, so the deployment must be:

* **repeatable**: the same steps on every server, no hand-edited configuration,
* **closed by default**: only what operators and vehicles need is reachable,
* **encrypted**: tokens and commands never cross the network in clear text,
* **recoverable**: losing the disk must not mean losing the flight history and audit log.

Questions to decide:

1. How does the stack run on the server (containers, systemd services, Kubernetes)?
2. What sits in front of the API (TLS, WebSockets, rate limits)?
3. Where do certificates come from, on the internet and on a closed network?
4. What is reachable from the network?
5. How are the database backups made, kept and restored?
6. How is it installed and how do we know the install works?

## Decision

### 1. Docker Compose on one host

| Option | For | Against |
|---|---|---|
| systemd units running `dotnet Gcs.Api.dll`, PostgreSQL and RabbitMQ from apt | no container runtime | a second way to run everything; versions differ from dev and CI |
| **Docker Compose** | **same images as CI's smoke test; one file describes the stack** | Docker's firewall behaviour (see 4) |
| Kubernetes (k3s) | scaling, rolling updates | a cluster to run and learn for one API instance |

One host is enough: the API holds the live vehicle links in memory (one process per set of vehicles, ADR-003), so a
second instance would need sticky vehicle ownership first. That is Phase 12 territory.

`deploy/compose.yml` is a separate file from the development `docker-compose.yml`. Differences:

* No port on 127.0.0.1 for PostgreSQL, RabbitMQ or the API. The databases sit on an **`internal: true` network**
  that has no route outside.
* No simulator, no PX4 SITL, no RabbitMQ management UI, no Redis (unused; ADR-011).
* The Aspire dashboard asks for its login token (no anonymous access) and is published on 127.0.0.1 only, for an SSH
  tunnel.
* Bounded container logs (`json-file`, 5 × 20 MB), `restart: unless-stopped`.
* Images are built on the server from the checked-out commit. Publishing images to a registry (GHCR) from CI is the
  next step once there are several servers; until then a build on the server keeps the setup to one moving part.

### 2. Nginx as the TLS-terminating reverse proxy

Kestrel can serve HTTPS itself, but a proxy in front gives us, in one well-known place: TLS configuration and
certificate reloads without restarting the API, a cheap per-address rate limit before requests reach .NET, request
logs with timing, and refusing connections for unknown host names (`ssl_reject_handshake`).

Settings worth recording:

* **TLS 1.2 and 1.3 only**, Mozilla "intermediate" ciphers, no session tickets, HTTP/2, HSTS for two years
  (set by Nginx; the API's copy is hidden so the header appears once).
* **WebSocket upgrade for SignalR** on `/hubs/`: `Upgrade`/`Connection` headers through a `map`, buffering off, a
  one-hour read timeout (SignalR pings every 15 s, so only dead connections hit it).
* **Rate limits per client address**: 10 sign-in attempts per minute (burst 5) on `/api/v1/auth/`, 20 requests per
  second (burst 40) elsewhere, 50 open connections. The API keeps its own finer limits (per user, per command); the
  two layers complement each other.
* `X-Forwarded-For` is **overwritten** with the address Nginx sees, never appended to, so a client cannot claim
  another address.

**The API trusts forwarded headers only from Nginx.** Behind a proxy every request comes from the
proxy's address; without forwarded headers, every client would share one login rate-limit bucket. The API now has
`ReverseProxy:Enabled` and `ReverseProxy:TrustedNetworks` (off by default). Compose gives the proxy network a fixed
subnet (`172.30.0.0/24`) and Nginx a fixed address in it (`172.30.0.10`, both configurable), and the API trusts that one
address (`/32`). Trusting the whole subnet would include its gateway, `172.30.0.1`, which is the host itself: the install
test showed requests from the host arriving from there. An integration test checks that two clients
behind the proxy get separate budgets and that headers from an untrusted address are ignored.

### 3. Certificates: Let's Encrypt or an internal CA

| Server | Choice |
|---|---|
| Public DNS name, internet access | **Let's Encrypt** with certbot, HTTP-01 challenge, standalone mode |
| Closed network (field, lab, no internet) | **Internal CA** created on the server (`deploy/tls/internal-ca.sh`) |

Let's Encrypt's HTTP-01 needs port 80. Port 80 stays closed: certbot's pre- and post-hooks open it in UFW only for
the seconds the challenge takes, on the first issue and on each renewal (certbot's systemd timer). A deploy hook copies
the renewed certificate to `/etc/gcs/tls` and reloads Nginx without dropping connections. DNS-01 would avoid port 80
but needs DNS provider credentials on the server; it can be added when a site needs it.

The internal CA uses ECDSA P-256 keys: a 10-year CA, server certificates of 397 days (the longest current clients
accept), re-issued automatically by a weekly cron job when fewer than 30 days remain. The CA is never replaced by a
re-run, so operator machines keep trusting it. Its key (`/etc/gcs/ca/ca.key`) must be backed up offline.

### 4. Firewall: UFW, and only two ports published

UFW denies all incoming traffic except **443/tcp** and the **MAVLink UDP port** (14550 by default). SSH stays open but
rate-limited (`ufw limit`) by default, because closing it on a remote server locks the administrator out. `--ssh CIDR`
restricts it to an admin network and `--ssh none` closes it for console-only servers.

**Docker bypasses UFW for published ports**: Docker writes its own iptables rules, which are evaluated before UFW's.
A `ports:` entry in compose is reachable from everywhere whatever UFW says. The real rule is therefore: compose
publishes only 443/tcp and the MAVLink port (plus the dashboard on 127.0.0.1). UFW protects the host's own services.
The install test checks the list of published ports.

Restricting MAVLink to the vehicles' network would need rules in Docker's `DOCKER-USER` chain. That is deferred to
Phase 12 (advanced networking), together with link encryption for MAVLink, which has no TLS of its own.

### 5. Backups: daily `pg_dump`, verified, kept 14 days

`gcs-backup` (cron, 03:00) runs `pg_dump --format=custom` inside the PostgreSQL container: one consistent snapshot
while the API keeps writing, compressed, restorable per table. It writes to a temporary name, verifies the dump with
`pg_restore --list` and only then renames it, so a half-written file never looks like a backup. Dumps older than the
retention (14 days by default) are deleted, **but never the newest one**: if backups stop, the last good one must not
age out. `gcs-restore FILE` stops the API, restores in one transaction and starts it again.

Continuous archiving (WAL, point-in-time recovery) would lose less than a day of data. For a GCS whose most valuable
data are the audit log and flight history, a daily dump is the agreed starting point. Copying the dumps off the server
(another host, object storage) is the operator's responsibility and is documented; a backup on the same disk does not
survive the disk.

### 6. One idempotent install script, verified in a throwaway Ubuntu container

`sudo deploy/install.sh --domain NAME --tls letsencrypt|internal` installs Docker from Docker's apt repository,
generates secrets into `/etc/gcs/gcs.env` (root only, never in git), issues the certificate, configures UFW, builds and
starts the stack, installs backups and runs an HTTPS smoke test. Running it again keeps secrets, the CA and the data,
and rebuilds from the current checkout: that is also the update procedure.

`deploy/test/verify-install.sh` proves it on a fresh Ubuntu 24.04 container with systemd (Docker in Docker,
privileged): HTTPS with the internal CA, HTTP/2, HSTS, sign-in, SignalR negotiate and WebSocket upgrade (101), the
Nginx 429, a refused handshake for unknown names, published ports, UFW rules, backup, retention, restore and an
idempotent re-run. It takes 10–15 minutes and is run before changing the deployment; it is not part of CI, because a
privileged Docker-in-Docker build of the whole stack would double the pipeline's time for files that rarely change.

## Consequences

* An operator can bring up a server with one command and knows exactly what is exposed.
* Two compose files must be kept in step when a service changes. The install test catches a broken production file.
* Let's Encrypt needs port 80 reachable for a few seconds every 60 days. Sites that cannot allow that use the
  internal CA or a future DNS-01 option.
* The internal CA's key is now a critical secret. Losing it means re-trusting a new CA on every operator machine.
* MAVLink over UDP remains unauthenticated and unencrypted on the network. MAVLink 2 message signing or a VPN to the
  vehicles is a Phase 12 decision.
