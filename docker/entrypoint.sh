#!/bin/sh
set -e

# Run the app as an arbitrary host uid/gid so it can write to bind-mounted volumes
# (notably the transcode cache) owned by that user. Defaults to 1000 to preserve the
# previous baked-in behaviour. Set PUID/PGID to match the owner of the mounted paths.
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

CACHE="${Transcoding__CachePath:-/app/transcode-cache}"

# Media is mounted read-only; only the transcode cache and the app HOME (for ASP.NET
# Data Protection keys) need to be writable by the runtime user. chown is best-effort
# so an already-correct or read-only mount never blocks startup.
mkdir -p "$CACHE" 2>/dev/null || true
chown -R "$PUID:$PGID" "$CACHE" 2>/dev/null || true
chown "$PUID:$PGID" /app 2>/dev/null || true

exec su-exec "$PUID:$PGID" dotnet Cosine.Api.dll
