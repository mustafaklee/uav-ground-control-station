#!/usr/bin/env bash
# Installs (or updates) the GCS backend on a clean Ubuntu Server 24.04 host. Phase 11, see docs/deployment.md.
#
#   git clone https://github.com/mustafaklee/uav-ground-control-station.git /opt/gcs
#   sudo /opt/gcs/deploy/install.sh --domain gcs.example.com --tls letsencrypt --email ops@example.com
#   sudo /opt/gcs/deploy/install.sh --domain gcs.lab.internal --tls internal
#
# Safe to run again: secrets, certificates and data are kept; images are rebuilt from the checkout and the stack is
# restarted. That is also how an update is done (git pull, then run this again).
#
# What it does, in order:
#   1. Docker Engine + Compose plugin from Docker's apt repository, ufw, cron, certbot (Let's Encrypt only)
#   2. /etc/gcs/gcs.env with generated secrets (database, broker, JWT key, first administrator)
#   3. A TLS certificate in /etc/gcs/tls: Let's Encrypt (HTTP-01), or our own internal CA
#   4. UFW: deny everything incoming, except 443/tcp, the MAVLink UDP port and (by default) SSH
#   5. The stack (deploy/compose.yml): build, migrate, start, wait until healthy
#   6. Daily PostgreSQL backup (pg_dump, cron) with retention; certificate renewal
#   7. A smoke test over HTTPS through Nginx
set -Eeuo pipefail

# ---- Defaults (all can be changed with options) ------------------------------------------------------------------
DOMAIN=""
TLS_MODE=""                # letsencrypt | internal
EMAIL=""                   # Let's Encrypt account (expiry warnings)
MAVLINK_PORT=14550
SSH_POLICY="limit"         # limit (open, rate-limited) | <CIDR> (only from there) | none (closed; console access only)
BACKUP_RETENTION_DAYS=14
BACKUP_HOUR=3              # local time; the dump takes seconds, but keep it away from flights
ADMIN_USERNAME="admin"
LE_STAGING=false
SKIP_FIREWALL=false        # only for a test container whose kernel has no netfilter support

CONFIG_DIR=/etc/gcs
ENV_FILE=$CONFIG_DIR/gcs.env
TLS_DIR=$CONFIG_DIR/tls
CA_DIR=$CONFIG_DIR/ca
BACKUP_DIR=/var/backups/gcs
DEPLOY_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
COMPOSE_FILE=$DEPLOY_DIR/compose.yml

usage() {
    cat <<EOF
Usage: sudo $0 --domain NAME --tls letsencrypt|internal [options]

  --domain NAME              DNS name operators use to reach the server (required)
  --tls letsencrypt          Certificate from Let's Encrypt. Needs --email, a public DNS record for NAME pointing
                             here, and port 80 reachable from the internet while certbot runs (opened only then).
  --tls internal             Certificate from a CA created on this server. For closed networks; install
                             $CA_DIR/ca.crt on the operator machines.
  --email ADDRESS            Let's Encrypt account e-mail
  --mavlink-port PORT        UDP port for MAVLink from the vehicles (default $MAVLINK_PORT)
  --ssh limit|CIDR|none      SSH: open but rate-limited (default), only from CIDR, or closed
  --backup-retention-days N  Keep daily database backups this many days (default $BACKUP_RETENTION_DAYS)
  --admin-username NAME      First administrator (default $ADMIN_USERNAME)
  --letsencrypt-staging      Use Let's Encrypt's staging CA (testing; the certificate is not trusted)
  --skip-firewall            Do not configure UFW (test containers only)
EOF
}

log()  { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33mWARNING: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }
trap 'die "line $LINENO: \"$BASH_COMMAND\" failed"' ERR

parse_args() {
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --domain) DOMAIN=${2:?}; shift 2 ;;
            --tls) TLS_MODE=${2:?}; shift 2 ;;
            --email) EMAIL=${2:?}; shift 2 ;;
            --mavlink-port) MAVLINK_PORT=${2:?}; shift 2 ;;
            --ssh) SSH_POLICY=${2:?}; shift 2 ;;
            --backup-retention-days) BACKUP_RETENTION_DAYS=${2:?}; shift 2 ;;
            --admin-username) ADMIN_USERNAME=${2:?}; shift 2 ;;
            --letsencrypt-staging) LE_STAGING=true; shift ;;
            --skip-firewall) SKIP_FIREWALL=true; shift ;;
            -h|--help) usage; exit 0 ;;
            *) usage; die "unknown option: $1" ;;
        esac
    done

    [[ -n $DOMAIN ]] || { usage; die "--domain is required"; }
    [[ $DOMAIN =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] || die "--domain must be a DNS name, got: $DOMAIN"
    case "$TLS_MODE" in
        letsencrypt) [[ -n $EMAIL ]] || die "--tls letsencrypt needs --email" ;;
        internal) ;;
        *) usage; die "--tls must be letsencrypt or internal" ;;
    esac
    [[ $MAVLINK_PORT =~ ^[0-9]+$ ]] && ((MAVLINK_PORT >= 1024 && MAVLINK_PORT <= 65535)) \
        || die "--mavlink-port must be 1024-65535"
    [[ $BACKUP_RETENTION_DAYS =~ ^[0-9]+$ ]] && ((BACKUP_RETENTION_DAYS >= 1)) \
        || die "--backup-retention-days must be a positive number"
}

preflight() {
    [[ $EUID -eq 0 ]] || die "run as root (sudo)"
    # shellcheck source=/dev/null
    . /etc/os-release
    if [[ ${ID:-} != ubuntu || ${VERSION_ID:-} != 24.04 ]]; then
        warn "tested on Ubuntu 24.04 only; this is ${PRETTY_NAME:-unknown}"
    fi
    [[ -f $COMPOSE_FILE ]] || die "run this script from a checkout of the repository ($COMPOSE_FILE is missing)"
}

# ---- 1. Packages -------------------------------------------------------------------------------------------------
install_packages() {
    log "Installing packages"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    apt-get install -y -q ca-certificates curl gnupg openssl ufw cron

    if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
        # Docker's own repository: Ubuntu's docker.io package lags behind and has no Compose v2 plugin.
        install -m 0755 -d /etc/apt/keyrings
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/ubuntu ${VERSION_CODENAME:-noble} stable" > /etc/apt/sources.list.d/docker.list
        apt-get update -q
        apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
    fi
    systemctl enable --now docker cron

    if [[ $TLS_MODE == letsencrypt ]]; then
        apt-get install -y -q certbot
    fi
}

# ---- 2. Configuration and secrets --------------------------------------------------------------------------------
secret() { openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-"${1:-32}"; }

set_env() { # set_env KEY VALUE: replace or append one line of the env file
    if grep -q "^$1=" "$ENV_FILE"; then
        sed -i "s|^$1=.*|$1=$2|" "$ENV_FILE"
    else
        echo "$1=$2" >> "$ENV_FILE"
    fi
}

write_config() {
    log "Writing $ENV_FILE"
    install -d -m 0750 "$CONFIG_DIR"
    if [[ ! -f $ENV_FILE ]]; then
        # Secrets are generated once, here, and never leave the server. Re-running keeps them: a new database
        # password would lock the API out of the existing database, a new JWT key would sign everybody out.
        umask 077
        cat > "$ENV_FILE" <<EOF
# GCS production configuration, written by deploy/install.sh. Readable by root only.
POSTGRES_USER=gcs
POSTGRES_PASSWORD=$(secret 32)
POSTGRES_DB=gcs
RABBITMQ_USER=gcs
RABBITMQ_PASSWORD=$(secret 32)
JWT_SIGNING_KEY=$(openssl rand -base64 48 | tr -d '\n')
# Used only while no user exists, to create the first administrator. Change the password after the first sign-in.
GCS_ADMIN_USERNAME=$ADMIN_USERNAME
GCS_ADMIN_PASSWORD=$(secret 24)
EOF
        umask 022
    fi
    chmod 0600 "$ENV_FILE"

    # Settings that follow the command line on every run.
    set_env GCS_DOMAIN "$DOMAIN"
    set_env GCS_MAVLINK_PORT "$MAVLINK_PORT"
    set_env GCS_TLS_MODE "$TLS_MODE"
    set_env BACKUP_RETENTION_DAYS "$BACKUP_RETENTION_DAYS"
    set_env GCS_DEPLOY_DIR "$DEPLOY_DIR"
    grep -q '^GCS_VERSION=' "$ENV_FILE" || set_env GCS_VERSION local
    # Change both together if 172.30.0.0/24 is already used on the site's network.
    grep -q '^GCS_PROXY_SUBNET=' "$ENV_FILE" || set_env GCS_PROXY_SUBNET 172.30.0.0/24
    grep -q '^GCS_PROXY_ADDRESS=' "$ENV_FILE" || set_env GCS_PROXY_ADDRESS 172.30.0.10

    # One command for operating the stack: gcs-compose ps, gcs-compose logs -f gcs-api, ...
    cat > /usr/local/bin/gcs-compose <<EOF
#!/bin/sh
exec docker compose --project-directory "$DEPLOY_DIR" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "\$@"
EOF
    chmod 0755 /usr/local/bin/gcs-compose
}

# ---- 3. TLS certificate ------------------------------------------------------------------------------------------
issue_internal_certificate() {
    install -d -m 0700 "$CA_DIR"
    install -d -m 0755 "$TLS_DIR"
    "$DEPLOY_DIR/tls/internal-ca.sh" --ca-dir "$CA_DIR" --out-dir "$TLS_DIR" --domain "$DOMAIN"
    # Re-issue before it expires (the script does nothing while more than 30 days are left).
    cat > /etc/cron.d/gcs-internal-cert <<EOF
# Renew the GCS server certificate from the internal CA. Written by deploy/install.sh.
17 4 * * 1 root "$DEPLOY_DIR/tls/internal-ca.sh" --ca-dir "$CA_DIR" --out-dir "$TLS_DIR" --domain "$DOMAIN" --reload 2>&1 | logger -t gcs-cert
EOF
}

issue_letsencrypt_certificate() {
    install -d -m 0755 "$TLS_DIR"
    install -d /etc/letsencrypt/renewal-hooks/pre /etc/letsencrypt/renewal-hooks/post /etc/letsencrypt/renewal-hooks/deploy

    # Port 80 is closed except while certbot answers the HTTP-01 challenge (certbot runs these on renewal too).
    cat > /etc/letsencrypt/renewal-hooks/pre/gcs-open-http.sh <<'EOF'
#!/bin/sh
command -v ufw >/dev/null && ufw status | grep -q "Status: active" && ufw allow 80/tcp comment 'certbot HTTP-01 (temporary)' || true
EOF
    cat > /etc/letsencrypt/renewal-hooks/post/gcs-close-http.sh <<'EOF'
#!/bin/sh
command -v ufw >/dev/null && ufw status | grep -q "Status: active" && ufw delete allow 80/tcp || true
EOF
    # A renewed certificate is copied to where Nginx reads it, then Nginx reloads without dropping connections.
    cat > /etc/letsencrypt/renewal-hooks/deploy/gcs-install-cert.sh <<EOF
#!/bin/sh
set -e
install -m 0644 "\$RENEWED_LINEAGE/fullchain.pem" "$TLS_DIR/fullchain.pem"
install -m 0600 "\$RENEWED_LINEAGE/privkey.pem" "$TLS_DIR/privkey.pem"
/usr/local/bin/gcs-compose exec -T nginx nginx -s reload 2>/dev/null || true
EOF
    chmod 0755 /etc/letsencrypt/renewal-hooks/*/gcs-*.sh

    if [[ ! -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem ]]; then
        local staging=()
        [[ $LE_STAGING == true ]] && staging=(--test-cert)
        /etc/letsencrypt/renewal-hooks/pre/gcs-open-http.sh
        certbot certonly --standalone --non-interactive --agree-tos -m "$EMAIL" -d "$DOMAIN" \
            --key-type ecdsa "${staging[@]}" || { /etc/letsencrypt/renewal-hooks/post/gcs-close-http.sh; die "certbot failed"; }
        /etc/letsencrypt/renewal-hooks/post/gcs-close-http.sh
    fi
    RENEWED_LINEAGE=/etc/letsencrypt/live/$DOMAIN /etc/letsencrypt/renewal-hooks/deploy/gcs-install-cert.sh
    # The certbot package's systemd timer runs "certbot renew" twice a day; the hooks above do the rest.
}

issue_certificate() {
    log "TLS certificate ($TLS_MODE)"
    if [[ $TLS_MODE == internal ]]; then issue_internal_certificate; else issue_letsencrypt_certificate; fi
}

# ---- 4. Firewall -------------------------------------------------------------------------------------------------
configure_firewall() {
    if [[ $SKIP_FIREWALL == true ]]; then
        warn "--skip-firewall: UFW is NOT configured"
        return
    fi
    log "Firewall (UFW)"
    ufw default deny incoming
    ufw default allow outgoing
    ufw allow 443/tcp comment 'GCS HTTPS (Nginx)'
    ufw allow "$MAVLINK_PORT"/udp comment 'GCS MAVLink'
    case "$SSH_POLICY" in
        limit) ufw limit 22/tcp comment 'SSH (rate-limited)' ;;
        none)  ufw delete limit 22/tcp >/dev/null 2>&1 || true
               ufw delete allow 22/tcp >/dev/null 2>&1 || true
               warn "SSH is closed by the firewall; use the server console" ;;
        *)     ufw allow from "$SSH_POLICY" to any port 22 proto tcp comment 'SSH (admin network)' ;;
    esac
    ufw --force enable
    ufw status verbose
}

# ---- 5. The stack ------------------------------------------------------------------------------------------------
start_stack() {
    log "Building images and starting the stack (the first build takes several minutes)"
    gcs-compose build --pull
    gcs-compose up -d --wait --wait-timeout 300 --remove-orphans
    gcs-compose ps
}

# ---- 6. Backups --------------------------------------------------------------------------------------------------
install_backups() {
    log "Daily database backup to $BACKUP_DIR (kept $BACKUP_RETENTION_DAYS days)"
    install -d -m 0700 "$BACKUP_DIR"
    install -m 0755 "$DEPLOY_DIR/backup/gcs-backup.sh" /usr/local/sbin/gcs-backup
    install -m 0755 "$DEPLOY_DIR/backup/gcs-restore.sh" /usr/local/sbin/gcs-restore
    cat > /etc/cron.d/gcs-backup <<EOF
# Daily PostgreSQL dump of the GCS database. Written by deploy/install.sh. Output goes to syslog (tag gcs-backup).
SHELL=/bin/sh
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
0 $BACKUP_HOUR * * * root gcs-backup 2>&1 | logger -t gcs-backup
EOF
    chmod 0644 /etc/cron.d/gcs-backup
}

# ---- 7. Smoke test -----------------------------------------------------------------------------------------------
smoke_test() {
    log "Smoke test: https://$DOMAIN/health/ready through Nginx"
    local ca=()
    [[ $TLS_MODE == internal ]] && ca=(--cacert "$CA_DIR/ca.crt")
    [[ $LE_STAGING == true ]] && ca=(--insecure)
    curl --fail --silent --show-error "${ca[@]}" --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/health/ready"
    echo
}

summary() {
    # shellcheck source=/dev/null
    . "$ENV_FILE"
    cat <<EOF

GCS is running.

  API (desktop client):  https://$DOMAIN/          e.g. Gcs.Desktop --api https://$DOMAIN/
  MAVLink:               UDP $MAVLINK_PORT          register vehicles with transport Udp, host 0.0.0.0, port $MAVLINK_PORT
  First administrator:   $GCS_ADMIN_USERNAME        password: grep GCS_ADMIN_PASSWORD $ENV_FILE  (change it after signing in)
  Operate:               gcs-compose ps | logs -f gcs-api | restart gcs-api
  Backups:               $BACKUP_DIR, daily at $BACKUP_HOUR:00; run now: sudo gcs-backup; restore: sudo gcs-restore FILE
  Dashboard:             ssh -L 18888:127.0.0.1:18888 <this server>, then http://localhost:18888
EOF
    if [[ $TLS_MODE == internal ]]; then
        cat <<EOF
  Internal CA:           copy $CA_DIR/ca.crt to every operator machine and trust it (docs/deployment.md).
                         Keep $CA_DIR/ca.key secret and backed up offline.
EOF
    fi
}

main() {
    parse_args "$@"
    preflight
    install_packages
    write_config
    issue_certificate
    configure_firewall
    start_stack
    install_backups
    smoke_test
    summary
}

main "$@"
