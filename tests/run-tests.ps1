[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'work\test-bin'
New-Item -ItemType Directory -Force -Path $work | Out-Null

Write-Host '=== 1. Python collector & statusline unit tests ==='
if (Get-Command python -ErrorAction SilentlyContinue) {
  & python -m unittest discover -s (Join-Path $root 'tests') -v
  if ($LASTEXITCODE -ne 0) { throw 'Python unit tests failed' }
} elseif (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
  $wslTests = (& wsl.exe wslpath -u (Join-Path $root 'tests')).Trim()
  & wsl.exe python3 -m unittest discover -s $wslTests -v
  if ($LASTEXITCODE -ne 0) { throw 'Python unit tests failed in WSL' }
} else {
  throw 'Python not found'
}

Write-Host '=== 2. Build production WPF overlay ==='
$prodExe = Join-Path $work 'AntigravityUsageOverlay.exe'
& (Join-Path $root 'scripts\build.ps1') -Output $prodExe

Write-Host '=== 3. Build & run C# regression tests ==='
$testExe = Join-Path $work 'OverlayTests.exe'
& (Join-Path $root 'scripts\build.ps1') -Output $testExe -Target exe -Main OverlayTests -ExtraSource @((Join-Path $root 'tests\OverlayTests.cs'))
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'C# OverlayTests failed' }

Write-Host '=== 4. Render offline dark/light preview ==='
$previewDir = Join-Path $work 'preview'
$proc = Start-Process -FilePath $prodExe -ArgumentList @('--render-preview', $previewDir) -Wait -PassThru
if ($proc.ExitCode -ne 0 -or -not (Test-Path (Join-Path $previewDir 'preview-dark.png')) -or -not (Test-Path (Join-Path $previewDir 'preview-light.png'))) {
  throw "Preview PNG generation failed (exit=$($proc.ExitCode))"
}

Write-Host 'All tests and builds passed.'
