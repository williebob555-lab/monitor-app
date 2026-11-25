# Monitor App peer node in a container, for Linux test servers.
# Uses the released, self-contained Ubuntu node package: no .NET SDK, same bits as the .deb.
# Build:  docker compose build            (version comes from MONITOR_APP_VERSION in .env)
# See docker/README.md for running and pairing.
FROM ubuntu:24.04

ARG MONITOR_APP_VERSION=0.1.35
ARG MONITOR_APP_REPOSITORY=williebob555-lab/monitor-app

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu74 libssl3t64 \
 && rm -rf /var/lib/apt/lists/*

# Download the node package and the release's SHA256SUMS, refuse to continue on a checksum mismatch,
# then unpack only the program files (no systemd units: Docker supervises the node here).
RUN set -eux; \
    base="https://github.com/${MONITOR_APP_REPOSITORY}/releases/download/preview-${MONITOR_APP_VERSION}"; \
    deb="monitorapp-node-ubuntu-x64-${MONITOR_APP_VERSION}.deb"; \
    cd /tmp; \
    curl -fsSLO "$base/$deb"; \
    curl -fsSLO "$base/SHA256SUMS"; \
    grep "  ${deb}\$" SHA256SUMS | sha256sum -c -; \
    dpkg-deb -x "$deb" /tmp/pkg; \
    mv /tmp/pkg/opt/monitor-app /opt/monitor-app; \
    mkdir -p /etc/monitor-app; \
    # A container is updated by rebuilding the image, not by the node's package updater: an empty trust anchor
    # makes the update agent report itself disabled instead of downloading packages it cannot install here.
    printf '{}\n' > /etc/monitor-app/update-key.json; \
    chmod 0644 /etc/monitor-app/update-key.json; \
    rm -rf /tmp/pkg "/tmp/$deb" /tmp/SHA256SUMS

# Unprivileged user, like the packaged service. Fixed uid so a bind-mounted data folder keeps working across rebuilds.
RUN groupadd --system --gid 10055 monitor-app \
 && useradd --system --uid 10055 --gid monitor-app --home-dir /var/lib/monitor-app --shell /usr/sbin/nologin monitor-app \
 && mkdir -p /var/lib/monitor-app \
 && chown monitor-app:monitor-app /var/lib/monitor-app \
 && chmod 0700 /var/lib/monitor-app

ENV MONITOR_APP_DATA_DIR=/var/lib/monitor-app \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER monitor-app
WORKDIR /var/lib/monitor-app
VOLUME ["/var/lib/monitor-app"]
LABEL org.opencontainers.image.title="Monitor App peer node" \
      org.opencontainers.image.source="https://github.com/williebob555-lab/monitor-app" \
      org.opencontainers.image.version="${MONITOR_APP_VERSION}"

# Healthy once the loopback admin API answers (host networking: this is the host's loopback).
HEALTHCHECK --interval=5m --start-interval=5s --timeout=5s --start-period=60s --retries=3 \
  CMD curl -fsS -o /dev/null "http://127.0.0.1:${MONITOR_APP_ADMIN_PORT:-55432}/api/runtime" || exit 1

ENTRYPOINT ["/opt/monitor-app/MonitorApp.Peer.Node"]
