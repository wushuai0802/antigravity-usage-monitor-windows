[CmdletBinding()]
param(
  [string]$StateDir = (Join-Path $env:LOCALAPPDATA 'AntigravityUsageMonitor')
)

$ErrorActionPreference = 'SilentlyContinue'
$overlay = Join-Path $StateDir 'bin\AntigravityUsageOverlay.exe'
if (-not (Test-Path -LiteralPath $overlay)) { exit 0 }
# Launch through explorer.exe so the overlay stays outside any agent process tree.
Start-Process explorer.exe -ArgumentList ('"' + $overlay + '"') -WindowStyle Hidden
