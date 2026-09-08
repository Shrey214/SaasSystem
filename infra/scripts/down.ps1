<#
.SYNOPSIS
    Stops the local infrastructure.

.PARAMETER Purge
    Also deletes the data volumes. Everything in every hs_* database is
    lost, and the init scripts will run again on the next start.

.EXAMPLE
    ./infra/scripts/down.ps1
    ./infra/scripts/down.ps1 -Purge
#>
param(
    [switch]$Purge
)

# NOTE: 'Continue', not 'Stop'.
# docker and psql write progress and expected failures to stderr. With
# 'Stop', PowerShell 5.1 wraps native stderr in an ErrorRecord and aborts
# the script even when the command exited 0. Exit codes are checked
# explicitly instead. See docs/learning/02-local-infrastructure.md
$ErrorActionPreference = 'Continue'

$compose = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\docker')) 'docker-compose.yml'

if ($Purge) {
    Write-Host 'This deletes every hs_* database and all their data.' -ForegroundColor Yellow
    $answer = Read-Host 'Type PURGE to confirm'
    if ($answer -ne 'PURGE') {
        Write-Host 'Cancelled - containers left running.' -ForegroundColor Cyan
        exit 0
    }
    docker compose -f $compose down -v 2>&1 | ForEach-Object { Write-Host $_ }
    Write-Host 'Stopped, volumes removed.' -ForegroundColor Green
}
else {
    docker compose -f $compose down 2>&1 | ForEach-Object { Write-Host $_ }
    Write-Host 'Stopped. Data kept - use -Purge to delete it.' -ForegroundColor Green
}
