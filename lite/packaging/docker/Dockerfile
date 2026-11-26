# syntax=docker/dockerfile:1
# Monitor App collector for a Linux server, in Docker. No window, no app: it
# writes this server's files into the shared Monitor folder like every other
# device. See README.md here for run and update steps.
FROM debian:12-slim

# "latest" = the newest Monitor App release (tag lite-v*). Or a number: 2.0.1
ARG VERSION=latest
ARG REPOSITORY=williebob555-lab/monitor-app

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl iputils-ping \
 && rm -rf /var/lib/apt/lists/*

# Download the collector and the release's SHA256SUMS; stop if they don't match.
# (A monitor-agent file next to this Dockerfile is used instead: CI tests.)
RUN --mount=type=bind,source=.,target=/ctx set -eux; \
    if [ -f /ctx/monitor-agent ]; then install -m 0755 /ctx/monitor-agent /usr/local/bin/monitor-agent; exit 0; fi; \
    v="$VERSION"; \
    if [ "$v" = latest ]; then \
      v=$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases?per_page=30" \
          | grep -o '"tag_name": *"lite-v[^"]*"' | head -n1 | sed 's/.*lite-v//; s/"$//'); \
    fi; \
    test -n "$v"; \
    base="https://github.com/$REPOSITORY/releases/download/lite-v$v"; \
    cd /tmp; \
    curl -fsSLO "$base/monitor-agent-$v-linux-x64"; \
    curl -fsSLO "$base/SHA256SUMS"; \
    grep "  monitor-agent-$v-linux-x64\$" SHA256SUMS | sha256sum -c -; \
    install -m 0755 "monitor-agent-$v-linux-x64" /usr/local/bin/monitor-agent; \
    rm -f /tmp/monitor-agent-* /tmp/SHA256SUMS; \
    /usr/local/bin/monitor-agent --version

# /monitor = the shared Monitor folder, /data = this install's id and state.
ENV HOME=/data MONITOR_FOLDER=/monitor MONITOR_DISK_PATH=/monitor
ENTRYPOINT ["/usr/local/bin/monitor-agent"]
