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
}
$exe = Get-ChildItem (Join-Path $repo 'src\Quartermaster.App\bin\Release') -Recurse -Filter Quartermaster.App.exe | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Quartermaster.App.exe (Release) not found' }
New-Item -ItemType Directory -Force $images | Out-Null

# NOTE: no outliner, batch, online, playerview or breakdown entries - their smokes assume the test fixture (a saved Setup, a Red/Blue pack), leave the map unpainted after the hot-seat hand-over, or (breakdown) draw a native tooltip that cannot be captured without copying the screen; Task 7 hand-shoots them.
# Name = output png (images/<Name>.png); Flag; Extra args; Smoke = seconds for --smoke (0 = the flag needs none);
# NoRules = no --rules; Docs = leave the sample chart/rules-text windows open (default: --no-smoke-documents keeps them out of the picture).
# A non-zero exit code is a failure, because the app's own self-checks failed. These runs are allowed to exit 1, each for the stated reason;
# the picture was checked by eye and shows the intended state.
# Each entry lists the exact app.log failure lines (regexes) that may appear; any other FAILED line still fails the run.
$allowedExit1 = @{
    'float'       = @{ Why = 'the focus-gate self-check assumes a docked map; this run floats it on purpose'
                       Lines = @('focus gate FAILED \(map is not the active MDI child\)') }
    'ledger-tabs' = @{ Why = 'the counter-pictures-drawn self-check races the picture worker; the shot itself waits for them'
                       Lines = @('ledger tabs check FAILED: counter pictures drawn$', 'ledger tabs FAILED$') }
    'phases'      = @{ Why = 'the turn-wrap and batch self-checks expect the test fixture''s phase names; the shots are taken before them'
                       Lines = @('turn wrap FAILED in \d+ step', 'turn wrap FAILED: run [^|]+ still pending in North Supply$','batch grid FAILED: the phase queued no run$') }
    'cheatsheet'  = @{ Why = 'same turn-wrap and batch self-checks as phases; the cheat sheet shots are taken before them'
                       Lines = @('turn wrap FAILED in \d+ step', 'turn wrap FAILED: run [^|]+ still pending in North Supply$', 'batch grid FAILED: the phase queued no run$') }
}
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
    @{ Name = 'handover';        Flag = '--screenshot-handover';    Smoke = 12 }
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
$marker = Join-Path $demoRoot '.quartermaster-demo-root'
$failed = @()
if (-not $Only) { Get-ChildItem $images -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force } # no stale pictures from earlier runs
function Reset-DemoRoot {
    # only ever delete a folder this script made (it holds the marker file)
    if ((Test-Path $demoRoot) -and -not (Test-Path $marker)) { throw "$demoRoot exists but was not made by this script; remove it by hand." }
    if (Test-Path $demoRoot) { Remove-Item $demoRoot -Recurse -Force }
}
foreach ($s in $shots) {
    # a fresh, empty profile for every run
    Reset-DemoRoot
    foreach ($d in 'AppData\Roaming', 'AppData\Local', 'Documents', 'layout') { New-Item -ItemType Directory -Force (Join-Path $demoRoot $d) | Out-Null }
    Set-Content -Path $marker -Value 'made by build/wiki-screenshots.ps1' -Encoding ascii
    $png = Join-Path $images ($s.Name + '.png')
    if (Test-Path $png) { Remove-Item $png -Force }

    $argList = @($vmod)
    if (-not $s.NoRules) { $argList += '--rules'; $argList += $rules }
    $argList += $s.Flag; $argList += $png
    if ($s.Extra) { $argList += $s.Extra }
    if ($s.Smoke -gt 0) { $argList += '--smoke'; $argList += "$($s.Smoke)"; if (-not $s.Docs) { $argList += '--no-smoke-documents' } }

    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.Arguments = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['APPDATA'] = Join-Path $demoRoot 'AppData\Roaming'
    $psi.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $demoRoot 'AppData\Local'
    $psi.EnvironmentVariables['USERPROFILE'] = $demoRoot
    $psi.EnvironmentVariables['QUARTERMASTER_LAYOUT_ROOT'] = Join-Path $demoRoot 'layout'
    # same number and date formats on every machine (no "1,20" from a local culture)
    $psi.EnvironmentVariables['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = '1'
    # every window of the run is created and kept far outside all screens, never activated (nothing shows on the desktop, no focus is taken)
    $psi.EnvironmentVariables['QUARTERMASTER_SHOT_OFFSCREEN'] = '1'
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        try { & taskkill /PID $p.Id /T /F | Out-Null } catch { }
        Write-Warning "$($s.Name): timed out after $TimeoutSeconds s (killed)"
        $failed += $s.Name; continue
    }
    $ok = (Test-Path $png) -and ((Get-Item $png).Length -gt 0)
    if (-not $ok) { Write-Warning "$($s.Name): no PNG produced (exit $($p.ExitCode))"; $failed += $s.Name; continue }
    $note = ''
    if ($p.ExitCode -ne 0) {
        $known = $allowedExit1[$s.Name]
        $log = Join-Path (Join-Path $demoRoot 'layout') 'app.log' #pp.log'
        $fails = if (Test-Path $log) { @(Get-Content $log | Where-Object { $_ -match 'FAILED' }) } else { @() }
        $unknown = if ($known) { @($fails | Where-Object { $line = $_; -not ($known.Lines | Where-Object { $line -match $_ }) }) } else { $fails }
        if ($p.ExitCode -ne 1 -or -not $known -or $fails.Count -eq 0 -or $unknown.Count -gt 0) {
            Write-Warning "$($s.Name): the app exited with code $($p.ExitCode); unexpected self-check failure(s): $($unknown -join ' || ')"
            $failed += $s.Name; continue
        }
        $note = '  exit 1 allowed: ' + $known.Why
    }
    Write-Host ("{0,-22} ok  {1} bytes{2}" -f $s.Name, (Get-Item $png).Length, $note)
}
Reset-DemoRoot
Write-Host ("Images in {0}: {1}" -f $images, (Get-ChildItem $images -Filter *.png | Measure-Object).Count)
if ($failed.Count) { Write-Warning ("Problems: " + ($failed -join ', ')); exit 1 }
