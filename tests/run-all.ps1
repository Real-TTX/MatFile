#requires -Version 5.1
<#
    Fuehrt alle Suiten hintereinander aus - jede gegen eine frische Datenbank.

    Standardmaessig laeuft das auf dem eigenen Teststack (Port 2223, eigenes Volume),
    damit der Entwicklungsstack mit seinen Speicherorten unangetastet bleibt.

        .\tests\run-all.ps1                 # Teststack auf Port 2223
        .\tests\run-all.ps1 -Suites test-search,test-remote
        .\tests\run-all.ps1 -Dev            # stattdessen den Entwicklungsstack (loescht dessen Daten!)
#>
param(
    [string[]]$Suites = @('test-auth', 'test-admin', 'test-storage', 'test-explorer',
                          'test-operations', 'test-preview', 'test-search', 'test-remote'),
    [switch]$Dev,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent

if ($Dev) {
    $compose = Join-Path $root 'docker-compose.dev.yml'
    $baseUrl = 'http://localhost:2222'
    $container = 'matfile-dev'
    $remotePrefix = 'matfile'
} else {
    $compose = Join-Path $root 'docker-compose.test.yml'
    $baseUrl = 'http://localhost:2223'
    $container = 'matfile-test'
    $remotePrefix = 'matfile-test'
}

function Reset-Stack {
    docker compose -f $compose --profile testing down -v 2>&1 | Out-Null

    if ($SkipBuild) { docker compose -f $compose up -d 2>&1 | Out-Null }
    else { docker compose -f $compose up -d --build 2>&1 | Out-Null }

    for ($i = 0; $i -lt 60; $i++) {
        try {
            $health = Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 3
            if ($health.status -eq 'healthy') { return $true }
        } catch { }
        Start-Sleep -Milliseconds 500
    }

    return $false
}

$summary = @()

foreach ($suite in $Suites) {
    $script = Join-Path $PSScriptRoot "$suite.ps1"
    if (-not (Test-Path $script)) { Write-Host "uebersprungen: $suite (nicht gefunden)"; continue }

    Write-Host ""
    Write-Host "=== $suite ===" -ForegroundColor Cyan

    if (-not (Reset-Stack)) {
        $summary += [pscustomobject]@{ Suite = $suite; Ergebnis = 'Stack nicht bereit'; OK = $false }
        continue
    }

    # Jede Suite bekommt nur die Parameter, die sie kennt.
    $arguments = @{ BaseUrl = $baseUrl }
    $known = (Get-Command $script).Parameters.Keys
    if ($known -contains 'Container') { $arguments['Container'] = $container }
    if ($known -contains 'Compose') { $arguments['Compose'] = $compose }
    if ($known -contains 'RemotePrefix') { $arguments['RemotePrefix'] = $remotePrefix }

    # Die Suiten schreiben ihre Zeilen mit Write-Host - deshalb auch Stream 6 einsammeln.
    $output = & $script @arguments 6>&1 2>&1 | ForEach-Object { "$_" }
    $output | ForEach-Object { Write-Host $_ }
    $code = $LASTEXITCODE

    $line = @($output | Where-Object { $_ -match '^Ergebnis:' })[-1]
    $summary += [pscustomobject]@{
        Suite = $suite
        Ergebnis = if ($line) { $line -replace '^Ergebnis:\s*', '' } else { 'kein Ergebnis' }
        OK = ($line -and @($output | Where-Object { $_ -match '^FAIL' }).Count -eq 0 -and $code -ne 1)
    }
}

Write-Host ""
Write-Host "=== Zusammenfassung ===" -ForegroundColor Cyan
$summary | ForEach-Object {
    $mark = if ($_.OK) { 'OK  ' } else { 'FAIL' }
    Write-Host "$mark  $($_.Suite)  $($_.Ergebnis)"
}

if (@($summary | Where-Object { -not $_.OK }).Count -gt 0) { exit 1 }
