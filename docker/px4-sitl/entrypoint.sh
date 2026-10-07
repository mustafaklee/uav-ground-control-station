#!/bin/sh
# Starts PX4 SITL (SIH physics, headless) and points its GCS MAVLink link at the GCS API instead of 127.0.0.1.
#
# PX4's GCS link sends to 127.0.0.1:14550 by default. Inside a container that is the container itself, so the
# address has to be replaced. PX4's mavlink module only accepts an IPv4 address (no host names), so the
# compose service name is resolved here, once, at start-up.
#
#   GCS_HOST  host name or IPv4 address of the GCS API (default gcs-api, the compose service)
#   GCS_PORT  UDP port the GCS listens on for this vehicle (default 14560)
#
# Any PX4 parameter can be set with PX4_PARAM_<NAME>=<value> (handled by PX4's own rcS).

set -eu

GCS_HOST="${GCS_HOST:-gcs-api}"
GCS_PORT="${GCS_PORT:-14560}"
RC_MAVLINK=/opt/px4/etc/init.d-posix/px4-rc.mavlink

gcs_ip=""
for attempt in 1 2 3 4 5 6 7 8 9 10; do
    gcs_ip=$(getent ahostsv4 "$GCS_HOST" 2>/dev/null | awk '/STREAM/ {print $1; exit}')
    [ -n "$gcs_ip" ] && break
    echo "INFO  [gcs] waiting for $GCS_HOST to resolve ($attempt/10)"
    sleep 2
done

if [ -z "$gcs_ip" ]; then
    echo "ERROR [gcs] cannot resolve $GCS_HOST to an IPv4 address" >&2
    exit 1
fi

# The GCS link is the first "mavlink start" line, the one with -u $udp_gcs_port_local. Add -t <ip> -o <port>.
sed -i "s|^mavlink start -x -u \$udp_gcs_port_local |mavlink start -x -t $gcs_ip -o $GCS_PORT -u \$udp_gcs_port_local |" "$RC_MAVLINK"
grep -q -- "-t $gcs_ip -o $GCS_PORT" "$RC_MAVLINK" || { echo "ERROR [gcs] could not patch $RC_MAVLINK" >&2; exit 1; }

echo "INFO  [gcs] MAVLink GCS link -> $GCS_HOST ($gcs_ip):$GCS_PORT/udp"
exec /opt/px4/bin/px4 "$@"
