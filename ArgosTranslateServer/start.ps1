<#
.SYNOPSIS
    Starts the Argos Translate server: a load balancer in front of translation worker processes
    (worker.py). It runs on its own; clients (the Translator app, Postman) only send requests to it.
    Stop it with Ctrl+C in this window or with stop.cmd.

    Settings come from config.json in this folder (port, workers, languages, ...).
    The options below override config.json for this run only. Works offline once setup.cmd has
    installed the packages and the language models.

.PARAMETER Port
    Port the load balancer listens on. Workers use the following ports (5001, 5002, ...).

.PARAMETER Workers
    Number of worker processes. Each needs about 1-1.5 GB of RAM and gets an equal share of the
    CPU cores. Rule of thumb: logical CPUs / 4 (e.g. 16 logical CPUs -> 4 workers).

.PARAMETER BindHost
    127.0.0.1 = only this PC. 0.0.0.0 = other PCs on the network can connect too
    (allow the port in the Windows firewall).

.PARAMETER Languages
    Comma-separated language codes, or "all" (only installed models can be used: install_models.cmd).

.PARAMETER Backends
    Comma-separated addresses of other translation servers to include in the load balancing,
    e.g. "http://server2:5000,http://server3:5000".

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File start.ps1                       # settings from config.json
    powershell -ExecutionPolicy Bypass -File start.ps1 -Workers 4 -BindHost 0.0.0.0
    powershell -ExecutionPolicy Bypass -File start.ps1 -Workers 1 -Backends "http://server2:5000"
#>
param(
    [int]$Port,
    [int]$Workers,
    [string]$BindHost,
    [string]$Languages,
    [string]$Backends
)

$ErrorActionPreference = "Stop"
$python = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"

if (-not (Test-Path $python)) {
    Write-Host "Argos Translate is not installed. Run setup.cmd first." -ForegroundColor Yellow
    exit 1
}

# Only the options given on the command line are passed; everything else comes from config.json
$arguments = @("-u", (Join-Path $PSScriptRoot "load_balancer.py"))
if ($PSBoundParameters.ContainsKey("Port"))      { $arguments += @("--port", "$Port") }
if ($PSBoundParameters.ContainsKey("Workers"))   { $arguments += @("--workers", "$Workers") }
if ($PSBoundParameters.ContainsKey("BindHost"))  { $arguments += @("--host", $BindHost) }
if ($PSBoundParameters.ContainsKey("Languages")) { $arguments += @("--languages", $Languages) }
if ($PSBoundParameters.ContainsKey("Backends"))  { $arguments += @("--backends", $Backends) }

Write-Host "Starting the Argos Translate server (settings: $(Join-Path $PSScriptRoot 'config.json'))"
Write-Host "Log: $(Join-Path $PSScriptRoot 'server.log'), worker logs: $(Join-Path $PSScriptRoot 'logs')"
Write-Host "Press Ctrl+C to stop (or run stop.cmd from another window)."
& $python @arguments
exit $LASTEXITCODE
