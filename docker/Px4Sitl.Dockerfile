# PX4 SITL with SIH physics (no Gazebo, no display), sending MAVLink over UDP to the GCS API.
# The official image is about 50 MB and starts in a few seconds, which makes a real autopilot practical in tests.
# Pinned to a release tag so a new PX4 build cannot change the behaviour of our tests overnight.
FROM px4io/px4-sitl:v1.18.0-rc1

COPY docker/px4-sitl/entrypoint.sh /opt/gcs/entrypoint.sh
RUN chmod +x /opt/gcs/entrypoint.sh

# MAVLink link to the GCS. 14560 keeps it apart from the built-in simulator on 14550.
ENV GCS_HOST=gcs-api \
    GCS_PORT=14560

ENTRYPOINT ["/opt/gcs/entrypoint.sh"]
# -d: daemon mode. Without it PX4 opens its interactive pxh> shell, which spins on a closed stdin and floods the log.
CMD ["-d"]
