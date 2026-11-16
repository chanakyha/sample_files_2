<#
.SYNOPSIS
    Stops the Argos Translate load balancer (and with it all its workers), however it was started.

    -Port defaults to the port in config.json.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File stop.ps1
    powershell -ExecutionPolicy Bypass -File stop.ps1 -Port 5001
#>
param([int]$Port)

if (-not $PSBoundParameters.ContainsKey("Port")) {
    $Port = 5000
    $config = Join-Path $PSScriptRoot "config.json"
    if (Test-Path $config) {
        $settings = Get-Content $config -Raw | ConvertFrom-Json
        if ($settings.port) { $Port = [int]$settings.port }
    }
}

try {
    $status = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/lb/status" -TimeoutSec 5
} catch {
    Write-Host "No load balancer is answering on port $Port."
    exit 0
}

if (-not $status.pid) {
    Write-Host "The server on port $Port did not report a process id (is it the load balancer?)." -ForegroundColor Yellow
    exit 1
}

# Workers are in the load balancer's Windows job object: they stop together with it
Stop-Process -Id $status.pid -Force
Write-Host "Stopped the load balancer (pid $($status.pid)) and its $($status.total) worker(s)."
