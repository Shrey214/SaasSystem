<#
.SYNOPSIS
    Starts the local infrastructure.

.PARAMETER Fresh
    Wipes the data volumes first, so the postgres init scripts run again.
    Needed after editing anything in infra/docker/postgres/init - those
    scripts only run on an empty volume.

.EXAMPLE
    ./infra/scripts/up.ps1
    ./infra/scripts/up.ps1 -Fresh
#>
param(
    # Which slice of the system to start. Profiles are what make 14 services
    # survivable on one laptop: "infra" is postgres + pgAdmin only, "core"
    # adds the services stage 4 needs, "full" is everything.
    [ValidateSet('infra', 'core', 'full')]
    [string]$Profile = 'infra',

    [switch]$Fresh,

    # Rebuild service images before starting. Needed after changing C#.
    [switch]$Build
)

# NOTE: 'Continue', not 'Stop'.
# docker and psql write progress and expected failures to stderr. With
# 'Stop', PowerShell 5.1 wraps native stderr in an ErrorRecord and aborts
# the script even when the command exited 0. Exit codes are checked
# explicitly instead. See docs/learning/02-local-infrastructure.md
$ErrorActionPreference = 'Continue'

$dockerDir = Resolve-Path (Join-Path $PSScriptRoot '..\docker')
$envFile = Join-Path $dockerDir '.env'

# Compose merges files. The base is always loaded; the services file is
# added whenever a profile beyond bare infrastructure is requested.
$composeArgs = @('-f', (Join-Path $dockerDir 'docker-compose.yml'))
if ($Profile -ne 'infra') {
    $composeArgs += @('-f', (Join-Path $dockerDir 'docker-compose.services.yml'))
    $composeArgs += @('--profile', $Profile)
}

if (-not (Test-Path $envFile)) {
    Copy-Item (Join-Path $dockerDir '.env.example') $envFile
    Write-Host "Created infra/docker/.env from .env.example." -ForegroundColor Yellow
    Write-Host "Passwords are placeholders - fine locally, change them before anything leaves this machine." -ForegroundColor Yellow
    Write-Host ''
}

if ($Fresh) {
    Write-Host 'Removing containers and data volumes...' -ForegroundColor Yellow
    docker compose @composeArgs down -v 2>&1 | ForEach-Object { Write-Host $_ }
    Write-Host ''
}

Write-Host 'Starting...' -ForegroundColor Cyan
$upArgs = $composeArgs + @('up', '-d')
if ($Build) { $upArgs += '--build' }

docker compose @upArgs 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    throw "docker compose up failed with exit code $LASTEXITCODE"
}

Write-Host ''
Write-Host 'Waiting for postgres to report healthy...' -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds(90)
do {
    $health = (docker inspect --format '{{.State.Health.Status}}' hs-postgres 2>$null)
    if ($health -eq 'healthy') { break }
    if ((Get-Date) -gt $deadline) {
        docker compose @composeArgs logs --tail 40 postgres 2>&1 | ForEach-Object { Write-Host $_ }
        throw "postgres did not become healthy within 90s (last status: $health)"
    }
    Start-Sleep -Milliseconds 1000
} while ($true)

Write-Host ''
docker compose @composeArgs ps 2>&1 | ForEach-Object { Write-Host $_ }

$pgadminPort = '5050'
$portMatch = @(Select-String -Path $envFile -Pattern '^PGADMIN_PORT=(.+)$')
if ($portMatch.Count -gt 0) { $pgadminPort = $portMatch[0].Matches[0].Groups[1].Value.Trim() }

$pgPort = '5433'
$pgPortMatch = @(Select-String -Path $envFile -Pattern '^POSTGRES_PORT=(.+)$')
if ($pgPortMatch.Count -gt 0) { $pgPort = $pgPortMatch[0].Matches[0].Groups[1].Value.Trim() }
Write-Host ''
Write-Host 'Ready.' -ForegroundColor Green
Write-Host "  pgAdmin   http://localhost:$pgadminPort"
Write-Host "  postgres  localhost:$pgPort (user postgres)"
Write-Host ''
if ($Profile -ne 'infra') {
    $tenantPort = '5101'
    $tp = @(Select-String -Path $envFile -Pattern '^TENANT_API_PORT=(.+)$')
    if ($tp.Count -gt 0) { $tenantPort = $tp[0].Matches[0].Groups[1].Value.Trim() }
    Write-Host "  tenant    http://localhost:$tenantPort/health"
}

Write-Host ''
Write-Host 'Verify the service boundary actually holds:' -ForegroundColor Cyan
Write-Host '  ./infra/scripts/verify-isolation.ps1' 

exit 0
