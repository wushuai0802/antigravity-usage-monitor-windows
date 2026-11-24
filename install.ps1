[CmdletBinding()]
param(
  [string]$InstallRoot = '',
  [switch]$SkipHooks,
  [switch]$SkipWslHooks,
  [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ($InstallRoot) {
  $StateRoot = [IO.Path]::GetFullPath($InstallRoot)
  $OverlayExe = Join-Path $StateRoot 'bin\AntigravityUsageOverlay.exe'
}

$scriptsDir = Join-Path $StateRoot 'scripts'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OverlayExe) | Out-Null
New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

# Build first so a failed compilation leaves any running overlay untouched.
$built = "$OverlayExe.new"
& (Join-Path $PSScriptRoot 'scripts\build.ps1') -Output $built

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'hooks\collect.py') -Destination (Join-Path $scriptsDir 'collect.py') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'hooks\statusline-sync.py') -Destination (Join-Path $scriptsDir 'statusline-sync.py') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'hooks\start-overlay.ps1') -Destination (Join-Path $scriptsDir 'start-overlay.ps1') -Force

if (Test-Path -LiteralPath $OverlayExe) {
  Copy-Item -LiteralPath $OverlayExe -Destination "$OverlayExe.previous" -Force
}
Stop-Overlay
try {
  Move-Item -LiteralPath $built -Destination $OverlayExe -Force
} catch {
  if (Test-Path -LiteralPath "$OverlayExe.previous") {
    Copy-Item -LiteralPath "$OverlayExe.previous" -Destination $OverlayExe -Force
  }
  if (-not $SkipLaunch) { Start-Process explorer.exe -ArgumentList ('"' + $OverlayExe + '"') -WindowStyle Hidden }
  throw
}

if (-not $SkipHooks) {
  $winHooks = Join-Path $env:USERPROFILE '.gemini\config\hooks.json'
  $winCollect = Join-Path $scriptsDir 'collect.py'
  Merge-HooksJson -HooksPath $winHooks -PythonCmd 'python' -CollectScriptPath $winCollect
  Write-Host "Updated Windows hooks: $winHooks"

  if (-not $SkipWslHooks -and (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    try {
      $wslCollect = (& wsl.exe wslpath -u $winCollect 2>$null | Select-Object -First 1)
      $wslState = (& wsl.exe wslpath -u $StateRoot 2>$null | Select-Object -First 1)
      if ($wslCollect) {
        $wslCollect = $wslCollect.Trim()
        $wslState = $wslState.Trim()
        $pyMerge = @"
import json, os, shutil
from pathlib import Path
hooks_path = Path.home() / '.gemini' / 'config' / 'hooks.json'
hooks_path.parent.mkdir(parents=True, exist_ok=True)
data = {}
if hooks_path.is_file():
    raw = hooks_path.read_text(encoding='utf-8').strip()
    if raw:
        shutil.copy2(hooks_path, str(hooks_path) + '.bak')
        data = json.loads(raw)
cmd = f'python3 "{os.environ["AGY_WSL_COLLECT"]}"'
data['antigravity-usage-monitor'] = {
    'enabled': True,
    'SessionStart': [{'type': 'command', 'command': cmd + ' --event SessionStart', 'timeout': 8}],
    'PreInvocation': [{'type': 'command', 'command': cmd + ' --event PreInvocation', 'timeout': 5}],
    'PostInvocation': [{'type': 'command', 'command': cmd + ' --event PostInvocation', 'timeout': 8}],
    'Stop': [{'type': 'command', 'command': cmd + ' --event Stop', 'timeout': 8}],
}
hooks_path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('Updated WSL hooks:', hooks_path)
"@
        & wsl.exe env "AGY_WSL_COLLECT=$wslCollect" python3 -c $pyMerge
        & wsl.exe python3 $wslCollect --scan-only --state-dir $wslState 2>$null
      }
    } catch {
      Write-Warning "WSL hook setup skipped: $($_.Exception.Message)"
    }
  }
}

if (-not $SkipLaunch) {
  Start-Process explorer.exe -ArgumentList ('"' + $OverlayExe + '"') -WindowStyle Hidden
}
Write-Host "Installed Antigravity Usage Monitor to $StateRoot"
Write-Host 'New Antigravity sessions will activate the hooks automatically.'
