#!/usr/bin/env bash
# A small certificate authority of our own, for servers on closed networks where Let's Encrypt cannot reach them
# (a field site, a lab, a network without internet). Phase 11, see docs/deployment.md and ADR-018.
#
#   internal-ca.sh --ca-dir /etc/gcs/ca --out-dir /etc/gcs/tls --domain gcs.lab.internal [--ip 10.0.0.5] [--reload]
#
# First run: creates the CA (ca.key, ca.crt; 10 years) and a server certificate signed by it (397 days).
# Later runs: do nothing while the server certificate is valid for more than 30 days and names the same domain;
# otherwise issue a new one. The CA itself is never replaced, so operator machines keep trusting it.
set -Eeuo pipefail

CA_DIR="" OUT_DIR="" DOMAIN="" IP="" RELOAD=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --ca-dir) CA_DIR=${2:?}; shift 2 ;;
        --out-dir) OUT_DIR=${2:?}; shift 2 ;;
        --domain) DOMAIN=${2:?}; shift 2 ;;
        --ip) IP=${2:?}; shift 2 ;;
        --reload) RELOAD=true; shift ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
[[ -n $CA_DIR && -n $OUT_DIR && -n $DOMAIN ]] || { echo "usage: $0 --ca-dir DIR --out-dir DIR --domain NAME [--ip ADDR] [--reload]" >&2; exit 2; }

umask 077
mkdir -p "$CA_DIR" "$OUT_DIR"

# ---- The CA ------------------------------------------------------------------------------------------------------
# ECDSA P-256: as strong as RSA-3072, smaller and faster, and supported by every TLS client this project has.
if [[ ! -f $CA_DIR/ca.key ]]; then
    echo "Creating the internal CA in $CA_DIR"
    openssl ecparam -name prime256v1 -genkey -noout -out "$CA_DIR/ca.key"
    openssl req -x509 -new -key "$CA_DIR/ca.key" -sha256 -days 3650 \
        -subj "/O=UAV GCS/CN=UAV GCS Internal CA" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" \
        -addext "subjectKeyIdentifier=hash" \
        -out "$CA_DIR/ca.crt"
    chmod 0644 "$CA_DIR/ca.crt" # public: this is the file operators install
fi

# ---- The server certificate --------------------------------------------------------------------------------------
cert=$OUT_DIR/fullchain.pem
if [[ -f $cert ]] \
    && openssl x509 -in "$cert" -noout -checkend $((30 * 24 * 3600)) >/dev/null \
    && openssl x509 -in "$cert" -noout -ext subjectAltName 2>/dev/null | grep -o 'DNS:[^, ]*' | grep -qxF "DNS:$DOMAIN"; then
    echo "Server certificate for $DOMAIN is valid for more than 30 days; nothing to do."
    exit 0
fi

echo "Issuing a server certificate for $DOMAIN"
san="DNS:$DOMAIN"
[[ -n $IP ]] && san="$san,IP:$IP"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$work/server.key"
openssl req -new -key "$work/server.key" -subj "/CN=$DOMAIN" -out "$work/server.csr"
cat > "$work/server.ext" <<EOF
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature
extendedKeyUsage=serverAuth
subjectAltName=$san
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid
EOF
# 397 days: the longest lifetime browsers and Apple/Microsoft clients accept for a server certificate.
openssl x509 -req -in "$work/server.csr" -CA "$CA_DIR/ca.crt" -CAkey "$CA_DIR/ca.key" -CAcreateserial \
    -days 397 -sha256 -extfile "$work/server.ext" -out "$work/server.crt"

# Nginx sends the whole chain; the CA certificate is what clients already trust.
cat "$work/server.crt" "$CA_DIR/ca.crt" > "$work/fullchain.pem"
install -m 0644 "$work/fullchain.pem" "$OUT_DIR/fullchain.pem"
install -m 0600 "$work/server.key" "$OUT_DIR/privkey.pem"
openssl x509 -in "$OUT_DIR/fullchain.pem" -noout -subject -enddate

if [[ $RELOAD == true ]] && command -v gcs-compose >/dev/null; then
    gcs-compose exec -T nginx nginx -s reload
fi
