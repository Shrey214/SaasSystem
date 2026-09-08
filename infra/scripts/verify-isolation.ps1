<#
.SYNOPSIS
    Proves stage 2 actually worked.

    Creating 13 databases is easy. The stage is only done if each service
    role can reach its own database and NOTHING else. This checks both
    directions, because a setup that passes the first and fails the second
    looks perfectly healthy while providing no isolation at all.

.EXAMPLE
    ./infra/scripts/verify-isolation.ps1
#>
# NOTE: 'Continue', not 'Stop'.
# docker and psql write progress and expected failures to stderr. With
# 'Stop', PowerShell 5.1 wraps native stderr in an ErrorRecord and aborts
# the script even when the command exited 0. Exit codes are checked
# explicitly instead. See docs/learning/02-local-infrastructure.md
$ErrorActionPreference = 'Continue'

$dockerDir = Resolve-Path (Join-Path $PSScriptRoot '..\docker')
$envFile = Join-Path $dockerDir '.env'

if (-not (Test-Path $envFile)) { throw 'infra/docker/.env not found - run up.ps1 first.' }

$pwMatch = @(Select-String -Path $envFile -Pattern '^HS_SERVICE_PASSWORD=(.*)$')
if ($pwMatch.Count -eq 0) { throw 'HS_SERVICE_PASSWORD not set in infra/docker/.env' }
$servicePassword = $pwMatch[0].Matches[0].Groups[1].Value.Trim()
if (-not $servicePassword) { throw 'HS_SERVICE_PASSWORD is empty in infra/docker/.env' }

$services = @(
    'identity', 'tenant', 'subscription', 'property', 'pricing', 'guest',
    'booking', 'payment', 'operations', 'notification', 'reporting',
    'audit', 'content'
)

function Invoke-AsRole {
    param([string]$Role, [string]$Database)

    # 127.0.0.1 forces a TCP connection so password auth is actually used.
    # Over the unix socket the container's local trust rules would apply.
    $null = docker exec -e "PGPASSWORD=$servicePassword" hs-postgres psql --username $Role --host 127.0.0.1 --dbname $Database --no-psqlrc --quiet --tuples-only --command 'select 1' 2>&1
    return $LASTEXITCODE -eq 0
}

Write-Host ''
Write-Host '--- 1. every database exists ------------------------------' -ForegroundColor Cyan

$existing = docker exec hs-postgres psql -U postgres --no-psqlrc --quiet --tuples-only --command "select datname from pg_database where datname like 'hs\_%' order by 1"
$existing = $existing | ForEach-Object { $_.Trim() } | Where-Object { $_ }

$missing = $services | Where-Object { "hs_$_" -notin $existing }
if ($missing) {
    Write-Host "  MISSING: $($missing -join ', ')" -ForegroundColor Red
}
else {
    Write-Host "  OK - all $($services.Count) databases present" -ForegroundColor Green
}
if ($existing -contains 'hs_stay') {
    Write-Host '  NOTE - hs_stay exists but should not until stage 14' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '--- 2. each role can reach its OWN database ---------------' -ForegroundColor Cyan

$ownFailures = @()
foreach ($svc in $services) {
    if (-not (Invoke-AsRole "hs_${svc}_user" "hs_$svc")) { $ownFailures += $svc }
}
if ($ownFailures) {
    Write-Host "  DENIED where it should be allowed: $($ownFailures -join ', ')" -ForegroundColor Red
}
else {
    Write-Host "  OK - all $($services.Count) roles can use their own database" -ForegroundColor Green
}

Write-Host ''
Write-Host '--- 3. no role can reach ANY other database ---------------' -ForegroundColor Cyan

$leaks = @()
foreach ($svc in $services) {
    foreach ($other in $services) {
        if ($other -eq $svc) { continue }
        if (Invoke-AsRole "hs_${svc}_user" "hs_$other") {
            $leaks += "hs_${svc}_user -> hs_$other"
        }
    }
}

Write-Host ''
if ($leaks) {
    Write-Host "FAILED - $($leaks.Count) cross-service connections succeeded:" -ForegroundColor Red
    $leaks | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'ADR-0003 is not being enforced. Check that 02-create-databases.sh ran' -ForegroundColor Red
    Write-Host 'its "revoke connect ... from public" line - init scripts only execute on' -ForegroundColor Red
    Write-Host 'an empty volume, so try: ./infra/scripts/up.ps1 -Fresh' -ForegroundColor Red
    exit 1
}

if ($missing -or $ownFailures) { exit 1 }

Write-Host "OK - all $($services.Count * ($services.Count - 1)) cross-service attempts were refused" -ForegroundColor Green
Write-Host ''
Write-Host 'Stage 2 verified: database-per-service is enforced by postgres, not by convention.' -ForegroundColor Green

# Explicit, because the last psql in check 3 is SUPPOSED to fail and would
# otherwise leave a non-zero $LASTEXITCODE as this script's exit code.
exit 0
