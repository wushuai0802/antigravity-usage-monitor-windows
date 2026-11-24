[CmdletBinding()]
param(
  [string]$InstallRoot = '',
  [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ($InstallRoot) {
  $StateRoot = [IO.Path]::GetFullPath($InstallRoot)
  $OverlayExe = Join-Path $StateRoot 'bin\AntigravityUsageOverlay.exe'
}

Stop-Overlay

$winHooks = Join-Path $env:USERPROFILE '.gemini\config\hooks.json'
Remove-HooksJsonEntry -HooksPath $winHooks

if (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
  $prevEap = $ErrorActionPreference
  try {
    $ErrorActionPreference = 'Continue'
    $pyRemove = @'
import json
from pathlib import Path
hooks_path = Path.home() / '.gemini' / 'config' / 'hooks.json'
if hooks_path.is_file():
    raw = hooks_path.read_text(encoding='utf-8').strip()
    if raw:
        data = json.loads(raw)
        if 'antigravity-usage-monitor' in data:
            del data['antigravity-usage-monitor']
            hooks_path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
'@
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pyRemove))
    & wsl.exe -e python3 -c "import base64; exec(base64.b64decode('$b64').decode('utf-8'))" 2>$null | Out-Null
  } catch {} finally {
    $ErrorActionPreference = $prevEap
  }
}

if ($RemoveData) {
  if (Test-Path -LiteralPath $StateRoot) {
    Remove-Item -LiteralPath $StateRoot -Recurse -Force
  }
} else {
  $binDir = Join-Path $StateRoot 'bin'
  $scriptsDir = Join-Path $StateRoot 'scripts'
  if (Test-Path -LiteralPath $binDir) { Remove-Item -LiteralPath $binDir -Recurse -Force }
  if (Test-Path -LiteralPath $scriptsDir) { Remove-Item -LiteralPath $scriptsDir -Recurse -Force }
}

Write-Host 'Uninstalled Antigravity Usage Monitor.'
