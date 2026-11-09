<#
.SYNOPSIS
  Side-by-side screenshots of the cross-platform Desktop app and the WinForms app on the invented Riverland demo.
.DESCRIPTION
  Stage 2a adds Desktop state shots (--screenshot-state: menu, hover, los, log; all with the Riverland rules pack) as
  artifacts/port/stage2a-desktop-<state>.png, and the WinForms equivalents where a flag exists (--screenshot-log, --screenshot-breakdown).
  Writes artifacts/port/stage1-desktop.png (Quartermaster.Desktop --screenshot-main, drawn headless with Avalonia.Headless: no window)
  and artifacts/port/stage1-winforms.png (Quartermaster.App --screenshot with QUARTERMASTER_SHOT_OFFSCREEN=1: windows are created far
  outside all screens and never activated). The WinForms run uses the same isolated profile folder as build/wiki-screenshots.ps1
  (C:\ProgramData\Quartermaster-Demo, wiped before and after). artifacts/ is gitignored.
.PARAMETER TimeoutSeconds
  Per-run time limit; a run that hangs is killed and reported. Default 120.
.PARAMETER SkipBuild
  Reuse the existing Release builds.
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 120,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'artifacts\port'
$vmod = Join-Path $repo 'docs\wiki\demo\Riverland-Demo.vmod'
$rules = Join-Path $repo 'docs\wiki\demo\Riverland-Demo.qrules'
$appProj = Join-Path $repo 'src\Quartermaster.App\Quartermaster.App.csproj'
$deskProj = Join-Path $repo 'src\Quartermaster.Desktop\Quartermaster.Desktop.csproj'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipBuild) {
    & dotnet build $appProj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'WinForms build failed' }
    & dotnet build $deskProj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
}
$appExe = Get-ChildItem (Join-Path $repo 'src\Quartermaster.App\bin\Release') -Recurse -Filter Quartermaster.App.exe | Select-Object -First 1 -ExpandProperty FullName
$deskExe = Get-ChildItem (Join-Path $repo 'src\Quartermaster.Desktop\bin\Release') -Recurse -Filter Quartermaster.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $appExe) { throw 'Quartermaster.App.exe (Release) not found' }
if (-not $deskExe) { throw 'Quartermaster.exe (Desktop, Release) not found' }

function Invoke-Shot([string]$Exe, [string[]]$ArgList, [hashtable]$Env, [string]$Png, [string]$Name) {
    if (Test-Path $Png) { Remove-Item $Png -Force }
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    $psi.Arguments = ($ArgList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = $Env[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { & taskkill /PID $p.Id /T /F | Out-Null } catch { }
        throw "$Name timed out after $TimeoutSeconds s (killed)"
    }
    if (-not (Test-Path $Png) -or (Get-Item $Png).Length -eq 0) { throw "$Name produced no PNG (exit $($p.ExitCode))" }
    if ($p.ExitCode -ne 0) { Write-Warning "$Name exited with code $($p.ExitCode); the picture exists, check it by eye." }
    Write-Host ("{0,-10} ok  {1} bytes  {2}" -f $Name, (Get-Item $Png).Length, $Png)
}

# Desktop: headless, no window. The Riverland demo has no predefined setups, so --new.
Invoke-Shot $deskExe @($vmod, '--new', '--screenshot-main', (Join-Path $out 'stage1-desktop.png')) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } (Join-Path $out 'stage1-desktop.png') 'desktop'

# Stage 2a states, Desktop, headless, with the demo's rules pack.
foreach ($state in 'menu', 'hover', 'los', 'log') {
    $png = Join-Path $out "stage2a-desktop-$state.png"
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png "desktop-$state"
}

# WinForms: off-screen, isolated profile (only ever deletes a folder this script family made).
$demoRoot = Join-Path $env:ProgramData 'Quartermaster-Demo'
$marker = Join-Path $demoRoot '.quartermaster-demo-root'
function Reset-DemoRoot {
    if ((Test-Path $demoRoot) -and -not (Test-Path $marker)) { throw "$demoRoot exists but was not made by build/wiki-screenshots.ps1 or this script; remove it by hand." }
    if (Test-Path $demoRoot) { Remove-Item $demoRoot -Recurse -Force }
}
Reset-DemoRoot
foreach ($d in 'AppData\Roaming', 'AppData\Local', 'Documents', 'layout') { New-Item -ItemType Directory -Force (Join-Path $demoRoot $d) | Out-Null }
Set-Content -Path $marker -Value 'made by build/port-screenshots.ps1' -Encoding ascii
try {
    $wfPng = Join-Path $out 'stage1-winforms.png'
    $env2 = @{
        APPDATA = Join-Path $demoRoot 'AppData\Roaming'
        LOCALAPPDATA = Join-Path $demoRoot 'AppData\Local'
        USERPROFILE = $demoRoot
        QUARTERMASTER_LAYOUT_ROOT = Join-Path $demoRoot 'layout'
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
        QUARTERMASTER_SHOT_OFFSCREEN = '1'
    }
    Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot', $wfPng, '--smoke', '8', '--no-smoke-documents') $env2 $wfPng 'winforms'
    # Stage 2a equivalents. WinForms has no flag for the context menu, LOS line or Tools menu (those are native/transient); the log and the
    # breakdown popup have one (the menu bar's popups are not drawn by the headless platform, so there is no Tools menu shot). Both stay off-screen because the main form is created off-screen.
    $logPng = Join-Path $out 'stage2a-winforms-log.png'
    Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot-log', $logPng, '--smoke', '8', '--no-smoke-documents') $env2 $logPng 'winforms-log'
    $bdPng = Join-Path $out 'stage2a-winforms-breakdown.png'
    Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot-breakdown', $bdPng, '--smoke', '8', '--no-smoke-documents') $env2 $bdPng 'winforms-breakdown'
}
finally { Reset-DemoRoot }
