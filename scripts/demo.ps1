param(
    [int]$Replicas = 2
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

docker compose up -d --build --scale "api=$Replicas" --wait
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

docker compose --profile demo build producer
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# --no-deps keeps the scaled API replicas. A plain run recreates dependencies at the default count.
docker compose --profile demo run --rm --no-deps producer
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

docker compose ps
exit $LASTEXITCODE
