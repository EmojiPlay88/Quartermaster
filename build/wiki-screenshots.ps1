<#
.SYNOPSIS
  Regenerates every wiki screenshot in docs/wiki/images from the invented Riverland demo module.
.DESCRIPTION
  Builds Quartermaster.App (Release), then runs it once per --screenshot-* flag against
  docs/wiki/demo/Riverland-Demo.vmod and .qrules. APPDATA, LOCALAPPDATA and USERPROFILE (and
  QUARTERMASTER_LAYOUT_ROOT, which holds all of the app's own files) point at a fresh temp folder named
  Quartermaster-Demo, so no recent files, paths or user names appear. Some flags write extra pictures next
  to the named one (for example <name>-panel.png); those are kept too.
.PARAMETER Only
  Optional list of image names (for example 'ledger','settings-display') to re-shoot instead of all.
.PARAMETER TimeoutSeconds
  Per-run time limit; a run that hangs is killed and reported. Default 120.
.PARAMETER SkipBuild
  Reuse the existing Release build.
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [int]$TimeoutSeconds = 120,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$images = Join-Path $repo 'docs\wiki\images'
$vmod = Join-Path $repo 'docs\wiki\demo\Riverland-Demo.vmod'
$rules = Join-Path $repo 'docs\wiki\demo\Riverland-Demo.qrules'
$proj = Join-Path $repo 'src\Quartermaster.App\Quartermaster.App.csproj'

if (-not $SkipBuild) {
    & dotnet build $proj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    # the online screenshot starts the relay found next to / above the App output
    & dotnet build (Join-Path $repo 'src\Quartermaster.Relay\Quartermaster.Relay.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'relay build failed' }
}
$exe = Get-ChildItem (Join-Path $repo 'src\Quartermaster.App\bin\Release') -Recurse -Filter Quartermaster.App.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Quartermaster.App.exe (Release) not found' }
New-Item -ItemType Directory -Force $images | Out-Null

# NOTE: no outliner, batch or online entries - their smokes assume the test fixture (a saved Setup, a Red/Blue pack), not the demo.
# Name = output png (images/<Name>.png); Flag; Extra args; Smoke = seconds for --smoke (0 = the flag needs none);
# NoRules = no --rules; Online = uses --smoke-online instead of --smoke.
$shots = @(
    @{ Name = 'main-window';     Flag = '--screenshot';             Smoke = 8 }
    @{ Name = 'float';           Flag = '--screenshot-float';       Smoke = 8; Extra = @('--float-main-map') }
    @{ Name = 'launcher';        Flag = '--screenshot-launcher';    Smoke = 0 }
    @{ Name = 'offline';         Flag = '--screenshot-offline';     Smoke = 0 }
    @{ Name = 'update-bar';      Flag = '--screenshot-update-bar';  Smoke = 0; NoRules = $true }
    @{ Name = 'setup';           Flag = '--screenshot-setup';       Smoke = 8 }
    @{ Name = 'ledger';          Flag = '--screenshot-ledger';      Smoke = 8 }
    @{ Name = 'ledger-tabs';     Flag = '--screenshot-ledger-tabs'; Smoke = 12 }
    @{ Name = 'workbench';       Flag = '--screenshot-workbench';   Smoke = 8 }
    @{ Name = 'workbench-tables';     Flag = '--screenshot-workbench'; Smoke = 8; Extra = @('--workbench-tab', 'tables') }
    @{ Name = 'workbench-formulas';   Flag = '--screenshot-workbench'; Smoke = 8; Extra = @('--workbench-tab', 'formulas') }
    @{ Name = 'workbench-properties'; Flag = '--screenshot-workbench'; Smoke = 8; Extra = @('--workbench-tab', 'properties') }
    @{ Name = 'workbench-phases';     Flag = '--screenshot-workbench'; Smoke = 8; Extra = @('--workbench-tab', 'phases') }
    @{ Name = 'search';          Flag = '--screenshot-search';      Smoke = 20 }
    @{ Name = 'ocr';             Flag = '--screenshot-ocr';         Smoke = 30 }
    @{ Name = 'turns';           Flag = '--screenshot-turns';       Smoke = 30 }
    @{ Name = 'settings';        Flag = '--screenshot-settings';    Smoke = 8 }
    @{ Name = 'alerts';          Flag = '--screenshot-alerts';      Smoke = 8 }
    @{ Name = 'phases';          Flag = '--screenshot-phases';      Smoke = 20; Extra = @('--smoke-phases') }
    @{ Name = 'palette';         Flag = '--screenshot-palette';     Smoke = 8; Extra = @('--gm') }
    @{ Name = 'overview';        Flag = '--screenshot-overview';    Smoke = 8 }
    @{ Name = 'notes';           Flag = '--screenshot-notes';       Smoke = 8 }
    @{ Name = 'inventory';       Flag = '--screenshot-inventory';   Smoke = 8 }
    @{ Name = 'formula';         Flag = '--screenshot-formula';     Smoke = 8 }
    @{ Name = 'breakdown';       Flag = '--screenshot-breakdown';   Smoke = 8 }
    @{ Name = 'handover';        Flag = '--screenshot-handover';    Smoke = 12 }
    @{ Name = 'playerview';      Flag = '--screenshot-playerview';  Smoke = 12 }
    @{ Name = 'tour';            Flag = '--screenshot-tour';        Smoke = 12 }
    @{ Name = 'stepback';        Flag = '--screenshot-stepback';    Smoke = 12 }
    @{ Name = 'nextphase';       Flag = '--screenshot-nextphase';   Smoke = 12 }
    @{ Name = 'cheatsheet';      Flag = '--screenshot-cheatsheet';  Smoke = 20; Extra = @('--smoke-phases', '--difficulty', 'semiauto') }
    @{ Name = 'log';             Flag = '--screenshot-log';         Smoke = 8 }
    @{ Name = 'export';          Flag = '--screenshot-export';      Smoke = 8 }
)
foreach ($t in 'general', 'game', 'display', 'map', 'dice', 'toolbar', 'keyboard', 'folders') {
    $shots += @{ Name = "settings-$t"; Flag = '--screenshot-settings'; Smoke = 8; Extra = @('--settings-tab', $t) }
}
if ($Only) { $Only = $Only -split ","; $shots = $shots | Where-Object { $Only -contains $_.Name } }

# Under ProgramData, not the temp folder: Settings > Folders draws this path, and the temp folder sits inside a user profile.
$demoRoot = Join-Path $env:ProgramData 'Quartermaster-Demo'
$failed = @()
foreach ($s in $shots) {
    # a fresh, empty profile for every run
    if (Test-Path $demoRoot) { Remove-Item $demoRoot -Recurse -Force }
    foreach ($d in 'AppData\Roaming', 'AppData\Local', 'Documents', 'layout') { New-Item -ItemType Directory -Force (Join-Path $demoRoot $d) | Out-Null }
    $expect = if ($s.Expect) { $s.Expect } else { $s.Name }
    $png = Join-Path $images ($expect + '.png')
    $arg = Join-Path $images ($s.Name + '.png')
    if (Test-Path $png) { Remove-Item $png -Force }

    $argList = @($vmod)
    if (-not $s.NoRules) { $argList += '--rules'; $argList += $rules }
    $argList += $s.Flag; $argList += $arg
    if ($s.Extra) { $argList += $s.Extra }
    if ($s.Online) { $argList += '--smoke-online'; $argList += "$($s.Smoke)"; $argList += '--smoke'; $argList += "$($s.Smoke)" }
    elseif ($s.Smoke -gt 0) { $argList += '--smoke'; $argList += "$($s.Smoke)" }

    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.Arguments = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['APPDATA'] = Join-Path $demoRoot 'AppData\Roaming'
    $psi.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $demoRoot 'AppData\Local'
    $psi.EnvironmentVariables['USERPROFILE'] = $demoRoot
    $psi.EnvironmentVariables['QUARTERMASTER_LAYOUT_ROOT'] = Join-Path $demoRoot 'layout'
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { & taskkill /PID $p.Id /T /F | Out-Null } catch { }
        Write-Warning "$($s.Name): timed out after $TimeoutSeconds s (killed)"
        $failed += $s.Name; continue
    }
    $ok = (Test-Path $png) -and ((Get-Item $png).Length -gt 0)
    if (-not $ok) { Write-Warning "$($s.Name): no PNG produced (exit $($p.ExitCode))"; $failed += $s.Name }
    else { Write-Host ("{0,-22} ok  exit {1}  {2} bytes" -f $s.Name, $p.ExitCode, (Get-Item $png).Length) }
}
if (Test-Path $demoRoot) { Remove-Item $demoRoot -Recurse -Force }
Write-Host ("Images in {0}: {1}" -f $images, (Get-ChildItem $images -Filter *.png | Measure-Object).Count)
if ($failed.Count) { Write-Warning ("Problems: " + ($failed -join ', ')); exit 1 }
