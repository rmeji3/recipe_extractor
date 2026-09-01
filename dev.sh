#!/usr/bin/env bash
# Starts everything the app needs, in dependency order, and stops it all on Ctrl-C.
#
# There are four moving parts (Postgres, Redis, the extraction sidecar, the API) plus the
# web client, and forgetting one produces a failure that looks like a bug in something
# else — an unreachable sidecar is indistinguishable from an unfetchable video.
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
LOGS="$ROOT/.dev-logs"
mkdir -p "$LOGS"

pids=()
cleanup() {
  echo
  echo "stopping…"
  for pid in "${pids[@]:-}"; do kill "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# `|| true` matters: lsof exits non-zero when nothing is listening, which under
# `set -e -o pipefail` would abort the script exactly when the port is free.
port_owner() { lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | head -1 || true; }

# Refuse to start something whose port is already taken. Otherwise the service below
# fails to bind, dies, and the health check happily passes against the *other* process —
# which then serves stale code and produces bugs that look like they are somewhere else.
require_free() { # port, name
  local owner
  owner=$(port_owner "$1")
  if [ -n "$owner" ]; then
    echo "  ✗ port $1 is already in use by pid $owner"
    echo "    Something else is serving $2. Stop it first, or that stale process will"
    echo "    answer requests meant for this one:  kill $owner"
    return 1
  fi
  # Explicit: a function ending on a false `if` returns 1, and `set -e` would kill the
  # script the moment a port turned out to be free.
  return 0
}

wait_for() { # url, name, seconds, pid
  for _ in $(seq 1 "$3"); do
    # The process dying is a faster and more certain answer than the timeout.
    if ! kill -0 "$4" 2>/dev/null; then
      echo "  ✗ $2 exited during startup — see $LOGS"
      return 1
    fi
    curl -sf "$1" >/dev/null 2>&1 && { echo "  ✓ $2"; return 0; }
    sleep 1
  done
  echo "  ✗ $2 did not come up — see $LOGS"
  return 1
}

echo "containers…"
docker compose -f server/docker-compose.yml up -d >/dev/null
for _ in $(seq 1 30); do
  [ "$(docker inspect --format '{{.State.Health.Status}}' recipe-postgres 2>/dev/null)" = healthy ] && break
  sleep 1
done
echo "  ✓ postgres and redis"

echo "sidecar…"
require_free 8000 "the sidecar"
( cd sidecar && exec "$ROOT/.venv/bin/uvicorn" app.main:app --port 8000 ) > "$LOGS/sidecar.log" 2>&1 &
sidecar_pid=$!
pids+=($sidecar_pid)
wait_for http://localhost:8000/health sidecar 60 "$sidecar_pid"

echo "api…"
require_free 5141 "the API"
( cd server && exec dotnet run --project Recipe.Api ) > "$LOGS/api.log" 2>&1 &
api_pid=$!
pids+=($api_pid)
wait_for http://localhost:5141/api/health api 90 "$api_pid"

if [ "${1:-}" != "--no-web" ]; then
  echo "web…"
  require_free 3000 "the web client"
  ( cd web && exec npm run dev ) > "$LOGS/web.log" 2>&1 &
  web_pid=$!
  pids+=($web_pid)
  wait_for http://localhost:3000 web 60 "$web_pid"
fi

cat <<MSG

  web      http://localhost:3000
  swagger  http://localhost:5141/swagger
  sidecar  http://localhost:8000/health
  logs     $LOGS

Ctrl-C to stop everything.
MSG

wait
