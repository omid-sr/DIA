param(
    [int]$Replicas = 2
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

if (-not (Test-Path ".env")) {
    Write-Error "Copy .env.example to .env and set WALLET_CONNECTION_STRING to your local SQL Server."
}

docker compose up -d --build --scale "api=$Replicas" --wait
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

docker compose ps
Write-Host ""
Write-Host "RabbitMQ is on localhost:5672. The two API replicas are behind http://localhost:8080."
Write-Host "In Visual Studio, set Producer as the startup project and press F5 to publish the charges."
exit 0
