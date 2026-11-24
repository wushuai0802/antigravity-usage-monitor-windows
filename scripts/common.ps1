$Script:StateRoot = Join-Path $env:LOCALAPPDATA 'AntigravityUsageMonitor'
$Script:OverlayExe = Join-Path $Script:StateRoot 'bin\AntigravityUsageOverlay.exe'
$Script:HookKey = 'antigravity-usage-monitor'

function Stop-Overlay {
  Get-Process -Name 'AntigravityUsageOverlay' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch {}
    try {
      if (-not $_.WaitForExit(1500)) { $_.Kill() }
    } catch {}
  }
}

function Merge-HooksJson {
  param(
    [Parameter(Mandatory)] [string]$HooksPath,
    [Parameter(Mandatory)] [string]$PythonCmd,
    [Parameter(Mandatory)] [string]$CollectScriptPath
  )
  $parent = Split-Path -Parent $HooksPath
  if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
  }
  $data = [ordered]@{}
  if (Test-Path -LiteralPath $HooksPath) {
    $raw = Get-Content -LiteralPath $HooksPath -Raw -Encoding UTF8
    if ($raw -and $raw.Trim()) {
      Copy-Item -LiteralPath $HooksPath -Destination "$HooksPath.bak" -Force
      $parsed = $raw | ConvertFrom-Json
      foreach ($prop in $parsed.PSObject.Properties) {
        $data[$prop.Name] = $prop.Value
      }
    }
  }
  $cmdBase = "$PythonCmd `"$CollectScriptPath`""
  $data[$Script:HookKey] = [ordered]@{
    enabled        = $true
    SessionStart   = @([ordered]@{ type = 'command'; command = "$cmdBase --event SessionStart"; timeout = 8 })
    PreInvocation  = @([ordered]@{ type = 'command'; command = "$cmdBase --event PreInvocation"; timeout = 5 })
    PostInvocation = @([ordered]@{ type = 'command'; command = "$cmdBase --event PostInvocation"; timeout = 8 })
    Stop           = @([ordered]@{ type = 'command'; command = "$cmdBase --event Stop"; timeout = 8 })
  }
  $json = $data | ConvertTo-Json -Depth 10
  [IO.File]::WriteAllText($HooksPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Remove-HooksJsonEntry {
  param([Parameter(Mandatory)] [string]$HooksPath)
  if (-not (Test-Path -LiteralPath $HooksPath)) { return }
  $raw = Get-Content -LiteralPath $HooksPath -Raw -Encoding UTF8
  if (-not $raw -or -not $raw.Trim()) { return }
  $parsed = $raw | ConvertFrom-Json
  $data = [ordered]@{}
  $removed = $false
  foreach ($prop in $parsed.PSObject.Properties) {
    if ($prop.Name -eq $Script:HookKey) {
      $removed = $true
      continue
    }
    $data[$prop.Name] = $prop.Value
  }
  if ($removed) {
    $json = $data | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($HooksPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
  }
}
