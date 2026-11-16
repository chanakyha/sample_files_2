<#
.SYNOPSIS
    Installs Argos Translate into a Python virtual environment (.venv) in this folder and downloads
    the language models from config.json into the models folder.

.DESCRIPTION
    Needs Python 3.10 - 3.13. The script looks for a supported version with the Python launcher (py),
    creates .venv, installs the packages from requirements.txt (about 1 GB, including PyTorch, which
    Argos Translate depends on) and then runs install_models.py.

    On a corporate network, run check_network.cmd first: it shows which download sites are reachable.

.PARAMETER Python
    Full path to a python.exe to use instead of searching for one.

.PARAMETER Proxy
    Proxy for the downloads on a corporate network, e.g. http://proxy.company.com:8080
    (default: "proxy" in config.json).

.PARAMETER SkipModels
    Only install the Python packages (download the models later with install_models.cmd).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup.ps1
    powershell -ExecutionPolicy Bypass -File setup.ps1 -Proxy http://proxy.company.com:8080
#>
param(
    [string]$Python = "",
    [string]$Proxy = "",
    [switch]$SkipModels
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$venv = Join-Path $here ".venv"
$venvPython = Join-Path $venv "Scripts\python.exe"

function Find-SupportedPython {
    foreach ($version in "3.12", "3.11", "3.13", "3.10") {
        try {
            $exe = & py "-$version" -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and $exe) { return $exe.Trim() }
        } catch { }
    }
    return $null
}

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $exe $($arguments -join ' ')" }
}

if (-not $Proxy) {
    $config = Join-Path $here "config.json"
    if (Test-Path $config) {
        $settings = Get-Content $config -Raw | ConvertFrom-Json
        if ($settings.proxy) { $Proxy = $settings.proxy }
    }
}

if (-not $Python) { $Python = Find-SupportedPython }
if (-not $Python) {
    Write-Host "No supported Python (3.10 - 3.13) found. Install Python 3.12, for example:" -ForegroundColor Yellow
    Write-Host "    winget install -e --id Python.Python.3.12 --scope user"
    Write-Host "Then run this script again."
    exit 1
}
Write-Host "Using Python: $Python"
if ($Proxy) { Write-Host "Using proxy: $Proxy" }

if (-not (Test-Path $venvPython)) {
    Write-Host "Creating virtual environment in $venv ..."
    Invoke-Checked $Python @("-m", "venv", $venv)
}

$pipArgs = @()
if ($Proxy) { $pipArgs += @("--proxy", $Proxy) }

Write-Host "Installing Argos Translate (about 1 GB, this can take several minutes) ..."
Invoke-Checked $venvPython (@("-m", "pip", "install", "--upgrade", "pip") + $pipArgs)
Invoke-Checked $venvPython (@("-m", "pip", "install", "-r", (Join-Path $here "requirements.txt")) + $pipArgs)

if (-not $SkipModels) {
    Write-Host ""
    Write-Host "Downloading the language models from config.json ..."
    $modelArgs = @("-u", (Join-Path $here "install_models.py"))
    if ($Proxy) { $modelArgs += @("--proxy", $Proxy) }
    Invoke-Checked $venvPython $modelArgs
}

Write-Host ""
Write-Host "Argos Translate is installed." -ForegroundColor Green
Write-Host "Start the server with:  start.cmd"
