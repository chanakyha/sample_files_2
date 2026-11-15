<#
.SYNOPSIS
    Installs LibreTranslate into a Python virtual environment (.venv) in this folder.

.DESCRIPTION
    LibreTranslate supports Python 3.8 - 3.13. The script looks for a supported version with the
    Python launcher (py), creates .venv and installs the packages from requirements.txt
    (about 1-2 GB including PyTorch).

.PARAMETER Python
    Full path to a python.exe to use instead of searching for one.

.PARAMETER Proxy
    Proxy for pip on a corporate network, e.g. http://proxy.company.com:8080

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup.ps1
#>
param(
    [string]$Python = "",
    [string]$Proxy = ""
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

if (-not $Python) { $Python = Find-SupportedPython }
if (-not $Python) {
    Write-Host "No supported Python (3.10 - 3.13) found. Install Python 3.12, for example:" -ForegroundColor Yellow
    Write-Host "    winget install -e --id Python.Python.3.12 --scope user"
    Write-Host "Then run this script again."
    exit 1
}
Write-Host "Using Python: $Python"

if (-not (Test-Path $venvPython)) {
    Write-Host "Creating virtual environment in $venv ..."
    Invoke-Checked $Python @("-m", "venv", $venv)
}

$pipArgs = @()
if ($Proxy) { $pipArgs += @("--proxy", $Proxy) }

Write-Host "Installing LibreTranslate (this can take several minutes) ..."
Invoke-Checked $venvPython (@("-m", "pip", "install", "--upgrade", "pip") + $pipArgs)
Invoke-Checked $venvPython (@("-m", "pip", "install", "-r", (Join-Path $here "requirements.txt")) + $pipArgs)

Write-Host ""
Write-Host "LibreTranslate is installed." -ForegroundColor Green
Write-Host "Start the server with:  powershell -ExecutionPolicy Bypass -File start.ps1"
Write-Host "(The first start downloads the language models.)"
