#!/usr/bin/env bash
# Runs deploy/install.sh in a throwaway Ubuntu 24.04 "server" container and checks the result from a second "client"
# container on the same network, the way an operator machine would see the server (Phase 11).
# Needs Docker on the machine running it. Takes 10-15 minutes the first time (packages, .NET image builds).
#
#   deploy/test/verify-install.sh          # install, check, leave the containers running for a look around
#   deploy/test/verify-install.sh --reuse  # skip the install, check the running server again
#   deploy/test/verify-install.sh --clean  # remove the containers, network and volumes afterwards
#
# Checks: HTTPS with the internal CA, HTTP/2, HSTS, sign-in, SignalR negotiate and WebSocket upgrade, Nginx rate limit,
# no TLS for unknown names, nothing else reachable, published ports, UFW rules, backup, retention, restore, re-run.
set -Eeuo pipefail

NAME=gcs-ubuntu-test
CLIENT=gcs-ubuntu-test-client
NETWORK=gcs-ubuntu-test-net
DOMAIN=gcs.test.internal
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
REUSE=false CLEAN=false
for arg in "$@"; do
    case "$arg" in
        --reuse) REUSE=true ;;
        --clean) CLEAN=true ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

pass() { printf '\033[1;32mPASS\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31mFAIL\033[0m %s\n' "$*"; exit 1; }
# Git Bash on Windows rewrites arguments like /opt/gcs into Windows paths; paths inside the containers must stay as is.
dk() { MSYS_NO_PATHCONV=1 docker "$@"; }
in_server() { dk exec "$NAME" bash -c "$*"; }
# curl on the client machine, trusting the internal CA, talking to https://$DOMAIN
https() { dk exec "$CLIENT" curl --cacert /ca.crt "$@"; }
url="https://$DOMAIN"

if [[ $REUSE == false ]]; then
    echo "==> Starting a fresh Ubuntu 24.04 server container and a client next to it"
    docker rm -f "$NAME" "$CLIENT" >/dev/null 2>&1 || true
    docker volume rm "$NAME-docker" "$NAME-containerd" >/dev/null 2>&1 || true
    # An explicit subnet, away from 172.17-31.x that Docker inside the server uses for its own networks.
    docker network create --subnet 10.231.0.0/24 "$NETWORK" >/dev/null 2>&1 || true
    docker build -q -t gcs-ubuntu-server:24.04 -f "$ROOT/deploy/test/ubuntu-server.Dockerfile" "$ROOT/deploy/test" >/dev/null
    # The server's name on the test network is the domain, like a DNS record pointing at a real server.
    dk run -d --name "$NAME" --hostname gcs-server --network "$NETWORK" --network-alias "$DOMAIN" \
        --privileged --cgroupns=private \
        -v "$NAME-docker:/var/lib/docker" -v "$NAME-containerd:/var/lib/containerd" \
        gcs-ubuntu-server:24.04 >/dev/null
    dk run -d --name "$CLIENT" --network "$NETWORK" --entrypoint sleep gcs-ubuntu-server:24.04 infinity >/dev/null
    for _ in $(seq 1 30); do
        state=$(dk exec "$NAME" systemctl is-system-running 2>/dev/null || true)
        [[ $state == running || $state == degraded ]] && break
        sleep 1
    done
    echo "systemd: $state"

    echo "==> Copying the checkout to /opt/gcs (tracked and new files; ignored ones like .env stay out)"
    in_server "mkdir -p /opt/gcs"
    (cd "$ROOT" && git ls-files -z --cached --others --exclude-standard | tar --null -T - -cf -) | dk exec -i "$NAME" tar -xf - -C /opt/gcs
    in_server "chmod +x /opt/gcs/deploy/*.sh /opt/gcs/deploy/*/*.sh"

    echo "==> Running install.sh"
    in_server "/opt/gcs/deploy/install.sh --domain $DOMAIN --tls internal --backup-retention-days 7"
fi

echo "==> Checks from the client"
# What an operator does once: install the CA certificate from the server.
in_server "cat /etc/gcs/ca/ca.crt" | dk exec -i "$CLIENT" sh -c 'cat > /ca.crt'

https -sf "$url/health/ready" >/dev/null || fail "/health/ready over HTTPS"
pass "HTTPS with the internal CA: /health/ready"
[[ $(https -s -o /dev/null -w '%{http_version}' --http2 "$url/health/live") == 2 ]] || fail "HTTP/2 not negotiated"
pass "HTTP/2"
https -sI "$url/health/live" | grep -qi '^strict-transport-security: max-age=63072000' || fail "HSTS header missing"
pass "HSTS header"
[[ $(https -s -o /dev/null -w '%{http_code}' "$url/api/v1/vehicles") == 401 ]] || fail "API not reachable through Nginx"
pass "API behind the proxy answers (401 without a token)"
dk exec "$CLIENT" curl -s -o /dev/null -k --resolve "other.example:443:$(dk exec "$CLIENT" getent hosts "$DOMAIN" | cut -d' ' -f1)" \
    "https://other.example/" && fail "an unknown server name got a TLS session"
pass "unknown server name: TLS handshake refused"
for port in 80 8080 5432 5672 15672 18888; do
    dk exec "$CLIENT" curl -s -o /dev/null --connect-timeout 3 "http://$DOMAIN:$port/" && fail "port $port answers from outside"
done
pass "nothing answers on 80, 8080, 5432, 5672, 15672, 18888"

# Sign in as the bootstrap administrator, then SignalR: negotiate, then the WebSocket upgrade.
password=$(in_server "grep '^GCS_ADMIN_PASSWORD=' /etc/gcs/gcs.env | cut -d= -f2-")
token=$(https -sf -X POST "$url/api/v1/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"$password\"}" | sed -nE 's/.*"accessToken":"([^"]+)".*/\1/p')
[[ -n $token ]] || fail "sign-in failed"
pass "sign-in through Nginx"
negotiate=$(https -sf -X POST "$url/hubs/telemetry/negotiate?negotiateVersion=1" -H "Authorization: Bearer $token")
connection=$(sed -nE 's/.*"connectionToken":"([^"]+)".*/\1/p' <<<"$negotiate")
[[ -n $connection ]] || fail "SignalR negotiate: $negotiate"
pass "SignalR negotiate"
upgrade=$(https -s --http1.1 --max-time 3 -o /dev/null -w '%{http_code}' \
    -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
    "$url/hubs/telemetry?id=$connection&access_token=$token" || true)
[[ $upgrade == 101 ]] || fail "WebSocket upgrade returned $upgrade"
pass "WebSocket upgrade on /hubs (101 Switching Protocols)"

# Nginx's own login limit: 10/min with a burst of 5, so 20 quick attempts must hit 429.
codes=$(for _ in $(seq 1 20); do
    https -s -o /dev/null -w '%{http_code}\n' -X POST "$url/api/v1/auth/login" \
        -H 'Content-Type: application/json' -d '{"username":"nobody","password":"wrong-password-123"}'
done)
grep -q 429 <<<"$codes" || fail "no 429 from 20 login attempts: $(tr '\n' ' ' <<<"$codes")"
pass "rate limit on /api/v1/auth (429 after the burst)"

echo "==> Checks on the server"
published=$(in_server "docker ps --format '{{.Ports}}' | tr ',' '\n' | grep -- '->' | sed 's/^ *//' | sort -u")
if grep -vqE '^(0\.0\.0\.0|\[::\]):(443->443/tcp|14550->14550/udp)$|^127\.0\.0\.1:18888->18888/tcp$' <<<"$published"; then
    fail "unexpected published ports: $published"
fi
pass "published: only 443/tcp, 14550/udp (and the dashboard on 127.0.0.1)"

rules=$(in_server "ufw status")
for rule in 'Status: active' '443/tcp' '14550/udp' '22/tcp.*LIMIT'; do
    grep -q "$rule" <<<"$rules" || fail "UFW: '$rule' missing in: $rules"
done
pass "UFW active: 443/tcp, 14550/udp, SSH rate-limited"

in_server "gcs-backup" || fail "gcs-backup"
dump=$(in_server "ls -1t /var/backups/gcs/gcs-*.dump | head -1")
pass "backup written: $dump"
in_server "touch -d '10 days ago' /var/backups/gcs/gcs-20000101T000000Z.dump && gcs-backup >/dev/null && test ! -e /var/backups/gcs/gcs-20000101T000000Z.dump" \
    || fail "retention"
pass "retention deletes dumps older than 7 days"
in_server "GCS_RESTORE_CONFIRM=yes gcs-restore $dump" >/dev/null || fail "restore"
https -sf "$url/health/ready" >/dev/null || fail "API not ready after restore"
pass "restore from the backup, API ready again"
in_server "grep -q gcs-backup /etc/cron.d/gcs-backup" || fail "backup cron entry"
pass "backup cron entry"

before=$(in_server "sha256sum /etc/gcs/gcs.env /etc/gcs/ca/ca.crt")
in_server "/opt/gcs/deploy/install.sh --domain $DOMAIN --tls internal --backup-retention-days 7" >/dev/null
[[ $(in_server "sha256sum /etc/gcs/gcs.env /etc/gcs/ca/ca.crt") == "$before" ]] || fail "re-run changed secrets or the CA"
https -sf "$url/health/ready" >/dev/null || fail "API not ready after the re-run"
pass "re-running install.sh keeps secrets and the CA, stack healthy again"

echo
echo "All checks passed."
if [[ $CLEAN == true ]]; then
    docker rm -f "$NAME" "$CLIENT" >/dev/null
    docker volume rm "$NAME-docker" "$NAME-containerd" >/dev/null
    docker network rm "$NETWORK" >/dev/null
else
    echo "The containers are still running: docker exec -it $NAME bash"
    echo "Remove them: deploy/test/verify-install.sh --reuse --clean, or docker rm -f $NAME $CLIENT"
fi
