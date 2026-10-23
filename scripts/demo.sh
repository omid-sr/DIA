#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

replicas="${1:-2}"

if [[ ! -f .env ]]; then
  echo "Copy .env.example to .env and set WALLET_CONNECTION_STRING to your local SQL Server." >&2
  exit 1
fi

docker compose up -d --build --scale "api=${replicas}" --wait
docker compose ps
echo
echo "RabbitMQ is on localhost:5672. The API replicas are behind http://localhost:8080."
echo "Run the Producer project from Visual Studio when you want to publish the charges."
