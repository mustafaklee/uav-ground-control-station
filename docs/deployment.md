# Deployment (Ubuntu Server 24.04)

How to run the GCS backend on a server that operators reach over the network. The decisions behind it are in
[ADR-018](adr/ADR-018-deployment.md); a step-by-step explanation in Turkish is in
[the Phase 11 guide](learning/phase-11-rehberi.md).

```
 operators (Gcs.Desktop)                       vehicles / modems
        │ HTTPS + WebSocket, 443/tcp                  │ MAVLink, 14550/udp
┌───────▼──────────────── Ubuntu Server 24.04 ────────▼───────────────────────┐
│  UFW: deny all incoming except 443/tcp, MAVLink udp, SSH (rate-limited)     │
│                                                                             │
│  nginx 172.30.0.10 ──(proxy network)──► gcs-api:8080 ◄── udp 14550          │
│  TLS, HTTP/2, WebSocket,                      │                             │
│  rate limit, HSTS                (backend network, internal: no outside)    │
│                                  postgres · rabbitmq · otel-dashboard       │
│                                                                             │
│  /etc/gcs/gcs.env (secrets)  /etc/gcs/tls  /var/backups/gcs (daily dumps)   │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Requirements

* Ubuntu Server 24.04 LTS, 2 vCPU, 4 GB RAM, 20 GB disk (the first image build needs most of the RAM).
* A DNS name for the server. For Let's Encrypt it must be public and point at the server, and port 80 must be
  reachable from the internet for a few seconds while certbot runs.
* Outbound internet during install (apt, Docker Hub, Microsoft Container Registry, NuGet). Air-gapped install from a
  registry mirror is not covered yet.

## Install

```bash
sudo git clone https://github.com/mustafaklee/uav-ground-control-station.git /opt/gcs

# Public server with Let's Encrypt
sudo /opt/gcs/deploy/install.sh --domain gcs.example.com --tls letsencrypt --email ops@example.com

# Closed network with our own CA
sudo /opt/gcs/deploy/install.sh --domain gcs.lab.internal --tls internal
```

Options (`install.sh --help`):

| Option | Default | Meaning |
|---|---|---|
| `--domain NAME` | (required) | Name operators use; the certificate is issued for it |
| `--tls letsencrypt\|internal` | (required) | Where the certificate comes from |
| `--email ADDRESS` | | Let's Encrypt account (expiry notices) |
| `--mavlink-port PORT` | 14550 | UDP port vehicles send MAVLink to |
| `--ssh limit\|CIDR\|none` | `limit` | SSH open but rate-limited, only from a network, or closed |
| `--backup-retention-days N` | 14 | How long daily dumps are kept |
| `--admin-username NAME` | `admin` | First administrator |
| `--letsencrypt-staging` | | Staging CA, for trying the Let's Encrypt flow without rate limits |

At the end the script prints the URL, the MAVLink port and where the first administrator's password is. Sign in with
the desktop client and **change that password** (`POST /api/v1/auth/password`).

What the install creates:

| Path | Content |
|---|---|
| `/etc/gcs/gcs.env` | Secrets and settings (0600 root). Generated once; kept on re-runs |
| `/etc/gcs/tls/` | `fullchain.pem`, `privkey.pem`, read by Nginx |
| `/etc/gcs/ca/` | Internal CA (`ca.crt` public, `ca.key` secret), internal mode only |
| `/var/backups/gcs/` | Daily dumps, `gcs-YYYYMMDDTHHMMSSZ.dump` (0700 root) |
| `/usr/local/bin/gcs-compose` | `docker compose` with the production file and env file |
| `/usr/local/sbin/gcs-backup`, `gcs-restore` | Backup and restore |
| `/etc/cron.d/gcs-backup`, `gcs-internal-cert` | Daily backup at 03:00; weekly certificate check (internal CA) |
| `/etc/letsencrypt/renewal-hooks/*/gcs-*.sh` | Open/close port 80 around renewals, install the renewed certificate |

## Connecting clients

**Desktop client:** `Gcs.Desktop --api https://gcs.example.com/` (or `GCS_API_URL`).

**Internal CA:** every operator machine must trust `/etc/gcs/ca/ca.crt` once. Copy it from the server
(`scp server:/etc/gcs/ca/ca.crt .`), then:

```powershell
# Windows (current user; no admin rights needed)
Import-Certificate -FilePath .\ca.crt -CertStoreLocation Cert:\CurrentUser\Root
```

```bash
# Ubuntu / Debian
sudo cp ca.crt /usr/local/share/ca-certificates/gcs-internal-ca.crt && sudo update-ca-certificates
```

**Vehicles:** send MAVLink to `<server>:14550/udp`. Register each vehicle in the GCS with transport `Udp`, host
`0.0.0.0` and port `14550`, then connect it.

## Operating

```bash
gcs-compose ps                     # state and health of every service
gcs-compose logs -f gcs-api        # API log (also in the dashboard)
gcs-compose logs -f nginx          # access log: address, status, time, correlation id
gcs-compose restart gcs-api
sudo ufw status verbose
```

**Dashboard** (traces, metrics, logs): `ssh -L 18888:127.0.0.1:18888 admin@server`, open http://localhost:18888 and
enter the token from `gcs-compose logs otel-dashboard | grep -m1 login`.

**Update** to a newer version:

```bash
cd /opt/gcs && sudo git pull
sudo deploy/install.sh --domain gcs.example.com --tls letsencrypt --email ops@example.com   # same options as before
```

Migrations run automatically (the `gcs-migrator` container) before the new API starts. Take a backup first
(`sudo gcs-backup`).

## Backups

* `sudo gcs-backup` writes a dump now; cron does it every day at 03:00 (`journalctl -t gcs-backup`).
* Each dump is checked with `pg_restore --list` before it counts. Dumps older than the retention are deleted; the newest
  one never is.
* **Copy the dumps off the server.** A backup on the same disk does not survive the disk. For example a nightly
  `rsync -a /var/backups/gcs/ backup-host:/srv/gcs/` from another machine.

Restore (stops the API, replaces the database in one transaction, starts the API):

```bash
sudo gcs-restore /var/backups/gcs/gcs-20261009T030000Z.dump
```

## Verifying the install script

`deploy/test/verify-install.sh` runs the whole install in a fresh Ubuntu 24.04 container (systemd as PID 1, Docker
inside it) and checks it from outside: HTTPS with the internal CA, HTTP/2, HSTS, sign-in through Nginx, SignalR
negotiate and WebSocket upgrade, the Nginx rate limit, refused TLS for unknown names, published ports, UFW rules,
backup, retention, restore, and that a second run keeps secrets and the CA. Run it after changing anything under
`deploy/` (10–15 minutes the first time):

```bash
deploy/test/verify-install.sh --clean
```

## Troubleshooting

| Symptom | Check |
|---|---|
| `install.sh` stops at the certificate (Let's Encrypt) | DNS points here? `dig +short gcs.example.com`. Port 80 reachable from outside (cloud security group)? |
| Browser or client: certificate not trusted | Internal CA: is `ca.crt` installed on that machine? Name in the URL equals `--domain`? |
| Desktop connects, live map never updates | WebSocket blocked by a proxy between client and server; `gcs-compose logs nginx \| grep hubs` |
| `429 Too Many Requests` | Nginx (`/api/v1/auth`: 10/min per address) or the API's per-user limits (`RateLimiting:*`) |
| Vehicle never connects | `sudo ufw status` shows the MAVLink port; vehicle sends to the right port; `gcs-compose logs gcs-api \| grep -i mavlink` |
| `gcs-api` unhealthy | `gcs-compose logs gcs-migrator gcs-api`; database password in `/etc/gcs/gcs.env` changed by hand? |
| Part of the site network unreachable from the server after install | Docker's networks overlap it. Set `GCS_PROXY_SUBNET` and `GCS_PROXY_ADDRESS` in `/etc/gcs/gcs.env` to a free range (and `default-address-pools` in `/etc/docker/daemon.json`), then re-run `install.sh` |
