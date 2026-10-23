#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

replicas="${1:-2}"

docker compose up -d --build --scale "api=${replicas}" --wait
docker compose --profile demo build producer
# --no-deps keeps the scaled API replicas. A plain `run` recreates dependencies at the default count.
docker compose --profile demo run --rm --no-deps producer
docker compose ps
