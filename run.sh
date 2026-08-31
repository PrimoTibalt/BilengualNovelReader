#!/usr/bin/env bash
#
# One-command start for the NovelReader web app.
#
#   ./run.sh              # http profile  -> http://localhost:5261/ReadingPage
#   ./run.sh --https      # https profile -> https://localhost:7178/ReadingPage
#   ./run.sh --stop       # stop the Mongo container and exit
#   ./run.sh --clean      # remove the Mongo container AND its data volume, then exit
#
# Does the three things `dotnet build` does not cover (see CLAUDE.md):
#   1. runs MongoDB in a rootless podman container matching appsettings.json
#   2. restores the SignalR client JS with libman (wwwroot/lib is gitignored)
#   3. builds + runs the app (the csproj compiles the TypeScript client itself)
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

MONGO_CONTAINER="${MONGO_CONTAINER:-novelreader-mongo}"
MONGO_VOLUME="${MONGO_VOLUME:-novelreader-mongo-data}"
MONGO_IMAGE="${MONGO_IMAGE:-docker.io/library/mongo:8}"
MONGO_PORT="${MONGO_PORT:-27017}"
MONGO_USER="${MONGO_USER:-admin}"
MONGO_PASSWORD="${MONGO_PASSWORD:-password}"
LAUNCH_PROFILE="http"

# Prints the comment banner at the top of this file, minus the leading "# ".
usage() {
  awk 'NR > 1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"
}

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m warn\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror\033[0m %s\n' "$*" >&2; exit 1; }

require() {
  command -v "$1" >/dev/null 2>&1 || die "$1 is required but not on PATH. $2"
}

stop_mongo() {
  if podman container exists "$MONGO_CONTAINER" 2>/dev/null; then
    log "Stopping $MONGO_CONTAINER"
    podman stop "$MONGO_CONTAINER" >/dev/null
  else
    log "No container named $MONGO_CONTAINER"
  fi
}

clean_mongo() {
  if podman container exists "$MONGO_CONTAINER" 2>/dev/null; then
    log "Removing container $MONGO_CONTAINER"
    podman rm -f "$MONGO_CONTAINER" >/dev/null
  fi
  if podman volume exists "$MONGO_VOLUME" 2>/dev/null; then
    log "Removing volume $MONGO_VOLUME (all cached chapters and progress)"
    podman volume rm "$MONGO_VOLUME" >/dev/null
  fi
}

while [ $# -gt 0 ]; do
  case "$1" in
    --https)  LAUNCH_PROFILE="https" ;;
    --http)   LAUNCH_PROFILE="http" ;;
    --stop)   require podman "Install it: sudo pacman -S podman"; stop_mongo; exit 0 ;;
    --clean)  require podman "Install it: sudo pacman -S podman"; clean_mongo; exit 0 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
  shift
done

# ---------------------------------------------------------------- prerequisites
require podman "Install it: sudo pacman -S podman"
require dotnet "Install the .NET 10 SDK plus: sudo pacman -S aspnet-runtime aspnet-targeting-pack"
require node   "The client TypeScript needs it: sudo pacman -S nodejs npm"

# ------------------------------------------------------------------------ mongo
if podman container exists "$MONGO_CONTAINER" 2>/dev/null; then
  if [ "$(podman inspect -f '{{.State.Running}}' "$MONGO_CONTAINER")" = "true" ]; then
    log "Mongo container $MONGO_CONTAINER already running"
  else
    log "Starting existing Mongo container $MONGO_CONTAINER"
    podman start "$MONGO_CONTAINER" >/dev/null
  fi
else
  log "Creating Mongo container $MONGO_CONTAINER on port $MONGO_PORT"
  podman run -d --name "$MONGO_CONTAINER" \
    -p "${MONGO_PORT}:27017" \
    -e MONGO_INITDB_ROOT_USERNAME="$MONGO_USER" \
    -e MONGO_INITDB_ROOT_PASSWORD="$MONGO_PASSWORD" \
    -v "${MONGO_VOLUME}:/data/db" \
    "$MONGO_IMAGE" >/dev/null
fi

log "Waiting for Mongo to accept connections"
for attempt in $(seq 1 60); do
  if podman exec "$MONGO_CONTAINER" mongosh --quiet \
       -u "$MONGO_USER" -p "$MONGO_PASSWORD" --authenticationDatabase admin \
       --eval 'db.adminCommand("ping").ok' >/dev/null 2>&1; then
    log "Mongo is ready"
    break
  fi
  if [ "$attempt" -eq 60 ]; then
    podman logs --tail 20 "$MONGO_CONTAINER" >&2 || true
    die "Mongo did not become ready in 60s. Logs above; 'podman logs $MONGO_CONTAINER' for more."
  fi
  sleep 1
done

# --------------------------------------------------------- signalr client (libman)
SIGNALR_JS="WebApi/wwwroot/lib/microsoft-signalr/signalr.min.js"
if [ ! -f "$SIGNALR_JS" ]; then
  LIBMAN="$(command -v libman || true)"
  [ -n "$LIBMAN" ] || LIBMAN="$HOME/.dotnet/tools/libman"
  if [ ! -x "$LIBMAN" ]; then
    log "Installing the libman CLI"
    dotnet tool install -g Microsoft.Web.LibraryManager.Cli >/dev/null
    LIBMAN="$HOME/.dotnet/tools/libman"
  fi
  log "Restoring the SignalR client into wwwroot/lib"
  (cd WebApi && "$LIBMAN" restore)
  [ -f "$SIGNALR_JS" ] || die "libman restore finished but $SIGNALR_JS is missing."
else
  log "SignalR client already present"
fi

# -------------------------------------------------------------------------- run
URL_HINT="http://localhost:5261/ReadingPage"
[ "$LAUNCH_PROFILE" = "https" ] && URL_HINT="https://localhost:7178/ReadingPage"

log "Starting the app ($LAUNCH_PROFILE profile) -> $URL_HINT"
log "Mongo keeps running after Ctrl-C; './run.sh --stop' shuts it down."
exec dotnet run --project WebApi --launch-profile "$LAUNCH_PROFILE"
