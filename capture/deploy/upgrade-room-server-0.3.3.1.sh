#!/usr/bin/env sh
set -eu

BRANCH="feature/arma3-team-sync-hud-monitor"
COMPOSE="capture/deploy/compose.yaml"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: Docker is required."
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "ERROR: Docker Compose v2 is required."
  exit 1
fi
if [ ! -f "$COMPOSE" ]; then
  echo "ERROR: run this script from the ODX-Capture-Development repository root."
  exit 1
fi
if [ -n "$(git status --porcelain)" ]; then
  echo "ERROR: repository has local changes. Back them up/commit them before updating."
  exit 1
fi

echo "Fetching the approved ODX Team Sync branch..."
git fetch origin "$BRANCH"
git checkout -B "$BRANCH" "origin/$BRANCH"

echo "Rebuilding only the Room orchestrator image..."
docker compose -f "$COMPOSE" build orchestrator

echo "Restarting only the Room orchestrator..."
docker compose -f "$COMPOSE" up -d --no-deps orchestrator

echo "Room orchestrator updated."
echo "The compose .env file and ./capture/deploy/state database volume were not edited or removed."
echo "Check logs with: docker compose -f $COMPOSE logs --tail=100 orchestrator"
