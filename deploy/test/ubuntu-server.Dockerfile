# A stand-in for a fresh Ubuntu Server 24.04 machine, to test deploy/install.sh without a VM (Phase 11).
# systemd runs as PID 1, like on a real server, so install.sh can enable Docker, cron and UFW the normal way.
# Docker then runs inside this container ("Docker in Docker"), which is why it needs --privileged, and why
# /var/lib/docker and /var/lib/containerd are volumes (overlayfs cannot be stacked on the container's own overlay).
# Used by deploy/test/verify-install.sh; not part of the production stack.
FROM ubuntu:24.04

ENV container=docker \
    DEBIAN_FRONTEND=noninteractive

# What a minimal server install already has (systemd, sudo, iptables, ssh client tools), nothing of ours.
RUN apt-get update \
 && apt-get install -y --no-install-recommends systemd systemd-sysv dbus sudo iproute2 iptables kmod \
      ca-certificates curl openssh-server procps \
 && apt-get clean && rm -rf /var/lib/apt/lists/* \
 && systemctl mask systemd-udevd.service systemd-udevd-kernel.socket systemd-udevd-control.socket \
      getty.target console-getty.service systemd-logind.service

STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
