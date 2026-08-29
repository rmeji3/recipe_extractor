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

wait_for() { # url, name, seconds
  for _ in $(seq 1 "$3"); do
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
( cd sidecar && exec "$ROOT/.venv/bin/uvicorn" app.main:app --port 8000 ) > "$LOGS/sidecar.log" 2>&1 &
pids+=($!)
wait_for http://localhost:8000/health sidecar 60

echo "api…"
( cd server && exec dotnet run --project Recipe.Api ) > "$LOGS/api.log" 2>&1 &
pids+=($!)
wait_for http://localhost:5141/api/health api 90

if [ "${1:-}" != "--no-web" ]; then
  echo "web…"
  ( cd web && exec npm run dev ) > "$LOGS/web.log" 2>&1 &
  pids+=($!)
  wait_for http://localhost:3000 web 60
fi

cat <<MSG

  web      http://localhost:3000
  swagger  http://localhost:5141/swagger
  sidecar  http://localhost:8000/health
  logs     $LOGS

Ctrl-C to stop everything.
MSG

wait
