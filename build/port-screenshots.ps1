<#
.SYNOPSIS
  Side-by-side screenshots of the cross-platform Desktop app and the WinForms app on the invented Riverland demo.
.DESCRIPTION
  Stage 2a adds Desktop state shots (--screenshot-state: menu, hover, los, log; all with the Riverland rules pack) as
  artifacts/port/stage2a-desktop-<state>.png, and the WinForms equivalents where a flag exists (--screenshot-log, --screenshot-breakdown).
  Stage 2b adds the Desktop shots artifacts/port/stage2b-desktop-<state>.png (phases, nextphase, stepback, formula, cheatsheet, batch, alerts, tree,
  floatlog, floatphases; dialogs and floating windows are drawn as their own picture, without a title bar) and the WinForms equivalents
  stage2b-winforms-<flag>.png (--screenshot-phases / nextphase / stepback / alerts).
  Stage 3a adds stage3a-desktop-<state>.png (ledger, ledger-counters, edittabs, outliner, inventory, notes, diagnostics, overview, export-choice) and
  stage3a-winforms-ledger.png.
  Stage 3b adds stage3b-desktop-<state>.png (chart, document, document-search) on an invented module (documents.vmod: one chart picture and a two-page
  invented rulebook PDF) that the script writes with the SmokeFixtureWriter test (QM_WRITE_SMOKE_FIXTURE) into a temp folder it deletes afterwards.
  Stage 4a adds stage4a-desktop-<state>.png (workbench, workbench-formulas, workbench-phases on the Riverland pack; workbench-search on the invented
  phases.vmod/phases.qrules rulebook with the query "supply"; workbench-scan on an invented table image the state draws with Skia and reads with the
  platform recognizer) and the WinForms equivalents stage4a-winforms-workbench-<tab>.png (--screenshot-workbench --workbench-tab tables|formulas|phases),
  stage4a-winforms-search.png (--screenshot-search) and stage4a-winforms-ocr.png (--screenshot-ocr), all off-screen.
  Stage 4b adds stage4b-desktop-settings[-<page>].png (the Settings window, one picture per page: general, game, display, map, dice, toolbar, keyboard,
  folders; run with a temp QUARTERMASTER_LAYOUT_ROOT so no real settings are touched) and the WinForms equivalents stage4b-winforms-settings-<page>.png
  (--screenshot-settings --settings-tab), all off-screen.
  Stage 4c adds stage4c-desktop-settings-ocr.png (Settings > OCR languages, real catalog, nothing downloaded) and stage4c-desktop-ocr-dropdown.png
  (the Workbench OCR language list open); both run with a temp QUARTERMASTER_LAYOUT_ROOT and an empty QUARTERMASTER_OCR_FEED folder (no network).
  Stage 5a adds stage5a-desktop-launcher.png, stage5a-desktop-offline.png and stage5a-desktop-update-bar.png (the pre-game windows: --screenshot-launcher,
  --screenshot-offline, --screenshot-update-bar; the module listed, an invented autosave and recent save for the offline window, a TEST folder feed
  offering 9.9.9 for the update bar, all inside a temp QUARTERMASTER_LAYOUT_ROOT; nothing reaches the network) and stage5a-desktop-tour.png (the Interface
  Tour balloon on step 1, captured as its own window).
  Stage 5b adds stage5b-desktop-handover.png (the hot-seat handover screen for Ben, captured as its own window), stage5b-desktop-players-page.png (the new-game
  Players page), stage5b-desktop-settings-players.png (Settings > Game with the Players box) and stage5b-desktop-player-menu.png (the status-bar player menu
  open); all on the Riverland pack with invented players Anna and Ben, inside a temp QUARTERMASTER_LAYOUT_ROOT.
  Stage 5c adds stage5c-desktop-<state>.png (online-lobby: the Online lobby window with invented TEST lobbies; online-host: a hosted game with its
  status text, a guest seat and a chat line; online-guest: a guest still joining; seats: the host's Players dialog; side-choice: the guest's side
  question). No network: the states use a null transport and invented rows, never Quartermaster.Relay. Temp QUARTERMASTER_LAYOUT_ROOT.
  Writes artifacts/port/stage1-desktop.png (Quartermaster.Desktop --screenshot-main, drawn headless with Avalonia.Headless: no window)
  and artifacts/port/stage1-winforms.png (Quartermaster.App --screenshot with QUARTERMASTER_SHOT_OFFSCREEN=1: windows are created far
  outside all screens and never activated). The WinForms run uses the same isolated profile folder as build/wiki-screenshots.ps1
  (C:\ProgramData\Quartermaster-Demo, wiped before and after). artifacts/ is gitignored.
.PARAMETER TimeoutSeconds
  Per-run time limit; a run that hangs is killed and reported. Default 120.
.PARAMETER SkipBuild
  Reuse the existing Release builds.
.PARAMETER Only
  Stage 5c state names (online-lobby, online-host, online-guest, seats, side-choice): run only those and stop (no WinForms, no other stage).
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 120,
    [switch]$SkipBuild,
    [string[]]$Only
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
    # Desktop runs get a fresh temp layout root unless the caller gave one, so remembered window layouts of the real profile cannot change a state.
    $ownLayout = $null
    if ($Exe -eq $deskExe -and -not $Env.ContainsKey('QUARTERMASTER_LAYOUT_ROOT')) {
        $ownLayout = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-run-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $ownLayout | Out-Null
        $Env = $Env.Clone(); $Env['QUARTERMASTER_LAYOUT_ROOT'] = $ownLayout
    }
    try {
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
    finally { if ($ownLayout -and (Test-Path $ownLayout)) { Remove-Item $ownLayout -Recurse -Force -ErrorAction SilentlyContinue } }
}

# Stage 5c states, Desktop, headless: the online screens, with invented TEST lobbies and a transport that goes nowhere (no network, no relay).
function Invoke-Stage5c([string[]]$States) {
    $layout5c = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-layout5c-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Force $layout5c | Out-Null
        $env5c = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layout5c }
        foreach ($state in $States) {
            $png = Join-Path $out "stage5c-desktop-$state.png"
            Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) $env5c $png "desktop-$state"
        }
    }
    finally { if (Test-Path $layout5c) { Remove-Item $layout5c -Recurse -Force } }
}
$states5c = 'online-lobby', 'online-host', 'online-guest', 'seats', 'side-choice'
if ($Only) {
    # powershell -File passes "a,b" as one string: split it, so both call styles name the same states.
    $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($Only | Where-Object { $states5c -notcontains $_ })
    if ($unknown.Count -gt 0) { throw "-Only: not a stage 5c state: $($unknown -join ', ') (known: $($states5c -join ', '))" }
    Invoke-Stage5c ($states5c | Where-Object { $Only -contains $_ }); return
}

# Desktop: headless, no window. The Riverland demo has no predefined setups, so --new.
Invoke-Shot $deskExe @($vmod, '--new', '--screenshot-main', (Join-Path $out 'stage1-desktop.png')) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } (Join-Path $out 'stage1-desktop.png') 'desktop'

# Stage 2a states, Desktop, headless, with the demo's rules pack.
foreach ($state in 'menu', 'hover', 'los', 'log') {
    $png = Join-Path $out "stage2a-desktop-$state.png"
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png "desktop-$state"
}

# Stage 2b states, Desktop, headless. Dialogs and floating windows are captured as their own window.
foreach ($state in 'phases', 'nextphase', 'stepback', 'formula', 'cheatsheet', 'batch', 'alerts', 'tree', 'floatlog', 'floatphases') {
    $png = Join-Path $out "stage2b-desktop-$state.png"
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png "desktop-$state"
}

# Stage 3a states, Desktop, headless: the Ledger (plain and with counter pictures), the tab editor, the docked outliner (main window picture),
# Inventory, Notes, Diagnostics, Overview and the "Save map as picture" choice. Each window is captured as its own picture.
foreach ($state in 'ledger', 'ledger-counters', 'edittabs', 'outliner', 'inventory', 'notes', 'diagnostics', 'overview', 'export-choice') {
    $png = Join-Path $out "stage3a-desktop-$state.png"
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png "desktop-$state"
}

# Stage 3b states, Desktop, headless: the chart window, the document window, and the document window on the rulebook page with a term highlighted.
# The Riverland demo has no charts or rulebooks, so these run on an invented module the SmokeFixtureWriter test writes. QUARTERMASTER_LAYOUT_ROOT points
# at a temp folder so the run does not write window positions into the real profile.
$fixDir = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-fixture-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:QM_WRITE_SMOKE_FIXTURE = $fixDir
    & dotnet test (Join-Path $repo 'tests\Quartermaster.Render.Tests') -c Release --nologo -v q --filter 'FullyQualifiedName~SmokeFixtureWriter'
    if ($LASTEXITCODE -ne 0) { throw 'Writing the documents fixture failed' }
    $env:QM_WRITE_SMOKE_FIXTURE = $null
    $docVmod = Join-Path $fixDir 'documents.vmod'
    if (-not (Test-Path $docVmod)) { throw "documents.vmod was not written to $fixDir" }
    $layoutTmp = Join-Path $fixDir 'layout'
    New-Item -ItemType Directory -Force $layoutTmp | Out-Null
    foreach ($state in 'chart', 'document', 'document-search') {
        $png = Join-Path $out "stage3b-desktop-$state.png"
        Invoke-Shot $deskExe @($docVmod, '--new', '--screenshot-state', $state, '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layoutTmp } $png "desktop-$state"
    }
}
finally {
    $env:QM_WRITE_SMOKE_FIXTURE = $null
    if (Test-Path $fixDir) { Remove-Item $fixDir -Recurse -Force }
}

# Stage 4a states, Desktop, headless. The workbench window is captured as its own picture. The search state runs on the invented phases fixture
# (a rulebook that mentions "supply"); the others on the Riverland pack. The scan state draws its own invented table image into a temp folder.
$fix4 = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-fixture4a-' + [Guid]::NewGuid().ToString('N'))
$phasesVmod = $null; $phasesRules = $null
try {
    $env:QM_WRITE_SMOKE_FIXTURE = $fix4
    & dotnet test (Join-Path $repo 'tests\Quartermaster.Render.Tests') -c Release --nologo -v q --filter 'FullyQualifiedName~SmokeFixtureWriter'
    if ($LASTEXITCODE -ne 0) { throw 'Writing the phases fixture failed' }
    $env:QM_WRITE_SMOKE_FIXTURE = $null
    $phasesVmod = Join-Path $fix4 'phases.vmod'
    $phasesRules = Join-Path $fix4 'phases.qrules'
    if (-not (Test-Path $phasesVmod) -or -not (Test-Path $phasesRules)) { throw "phases.vmod / phases.qrules were not written to $fix4" }
    $layout4 = Join-Path $fix4 'layout'
    New-Item -ItemType Directory -Force $layout4 | Out-Null
    $env4 = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layout4 }
    foreach ($state in 'workbench', 'workbench-formulas', 'workbench-phases', 'workbench-scan') {
        $png = Join-Path $out "stage4a-desktop-$state.png"
        Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) $env4 $png "desktop-$state"
    }
    $png = Join-Path $out 'stage4a-desktop-workbench-search.png'
    Invoke-Shot $deskExe @($phasesVmod, '--new', '--rules', $phasesRules, '--screenshot-state', 'workbench-search', '--screenshot-main', $png) $env4 $png 'desktop-workbench-search'

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
        $env4wf = @{
            APPDATA = Join-Path $demoRoot 'AppData\Roaming'; LOCALAPPDATA = Join-Path $demoRoot 'AppData\Local'; USERPROFILE = $demoRoot
            QUARTERMASTER_LAYOUT_ROOT = Join-Path $demoRoot 'layout'; DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_SHOT_OFFSCREEN = '1'
        }
        foreach ($tab in 'tables', 'formulas', 'phases') {
            $png = Join-Path $out "stage4a-winforms-workbench-$tab.png"
            try { Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot-workbench', $png, '--workbench-tab', $tab, '--smoke', '8', '--no-smoke-documents') $env4wf $png "winforms-workbench-$tab" }
            catch { Write-Warning "winforms-workbench-${tab}: $($_.Exception.Message)" }
        }
        $png = Join-Path $out 'stage4a-winforms-search.png'
        try { Invoke-Shot $appExe @($phasesVmod, '--rules', $phasesRules, '--smoke-search', '--screenshot-search', $png, '--smoke', '8') $env4wf $png 'winforms-search' }
        catch { Write-Warning "winforms-search: $($_.Exception.Message)" }
        $png = Join-Path $out 'stage4a-winforms-ocr.png'
        try { Invoke-Shot $appExe @($phasesVmod, '--rules', $phasesRules, '--smoke-ocr', '--screenshot-ocr', $png, '--smoke', '8') $env4wf $png 'winforms-ocr' }
        catch { Write-Warning "winforms-ocr: $($_.Exception.Message)" }
    }
    finally { Reset-DemoRoot }
}
finally {
    $env:QM_WRITE_SMOKE_FIXTURE = $null
    if (Test-Path $fix4) { Remove-Item $fix4 -Recurse -Force }
}

# Stage 4b states, Desktop, headless: the Settings window, one picture per page (the window is captured as its own picture). A temp layout root
# keeps the run away from the real settings.json.
$layout4b = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-layout4b-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force $layout4b | Out-Null
    $env4b = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layout4b }
    foreach ($state in 'settings', 'settings-general', 'settings-game', 'settings-display', 'settings-map', 'settings-dice', 'settings-toolbar', 'settings-keyboard', 'settings-folders') {
        $png = Join-Path $out "stage4b-desktop-$state.png"
        Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) $env4b $png "desktop-$state"
    }
}
finally { if (Test-Path $layout4b) { Remove-Item $layout4b -Recurse -Force } }

# Stage 4c states, Desktop, headless: the OCR languages settings page and the Workbench OCR language list, both with the real catalog and nothing
# downloaded (eng Bundled, the rest Not installed). A temp layout root keeps the run away from real settings and downloads; QUARTERMASTER_OCR_FEED
# points at an empty folder so nothing can reach the network.
$layout4c = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-layout4c-' + [Guid]::NewGuid().ToString('N'))
$feed4c = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-feed4c-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force $layout4c, $feed4c | Out-Null
    $env4c = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layout4c; QUARTERMASTER_OCR_FEED = $feed4c }
    $png = Join-Path $out 'stage4c-desktop-settings-ocr.png'
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', 'settings-ocr', '--screenshot-main', $png) $env4c $png 'desktop-settings-ocr'
    $png = Join-Path $out 'stage4c-desktop-ocr-dropdown.png'
    # No invariant globalization here: Windows OCR display names ("English (United States)") need ICU culture data; invariant mode shows raw tags.
    $env4cDrop = @{ QUARTERMASTER_LAYOUT_ROOT = $layout4c; QUARTERMASTER_OCR_FEED = $feed4c }
    Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', 'ocr-dropdown', '--screenshot-main', $png) $env4cDrop $png 'desktop-ocr-dropdown'
}
finally { foreach ($d in $layout4c, $feed4c) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } } }

# Stage 5a, Desktop, headless: the launcher, Play offline and the launcher with the update bar (each run gets its own temp layout root, which the
# pre-game flags require; the update bar's TEST feed is written inside it), and the Interface Tour balloon on step 1.
foreach ($shot in @(@('launcher', '--screenshot-launcher'), @('offline', '--screenshot-offline'), @('update-bar', '--screenshot-update-bar'))) {
    $png = Join-Path $out "stage5a-desktop-$($shot[0]).png"
    Invoke-Shot $deskExe @($vmod, $shot[1], $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png "desktop-$($shot[0])"
}
$png = Join-Path $out 'stage5a-desktop-tour.png'
Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', 'tour', '--screenshot-main', $png) @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } $png 'desktop-tour'

# Stage 5b states, Desktop, headless: the hot-seat screens (two invented players on the Riverland pack; a temp layout root keeps the run away from real settings).
$layout5b = Join-Path ([IO.Path]::GetTempPath()) ('qm-shot-layout5b-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force $layout5b | Out-Null
    $env5b = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; QUARTERMASTER_LAYOUT_ROOT = $layout5b }
    foreach ($state in 'handover', 'players-page', 'settings-players', 'player-menu') {
        $png = Join-Path $out "stage5b-desktop-$state.png"
        Invoke-Shot $deskExe @($vmod, '--new', '--rules', $rules, '--screenshot-state', $state, '--screenshot-main', $png) $env5b $png "desktop-$state"
    }
}
finally { if (Test-Path $layout5b) { Remove-Item $layout5b -Recurse -Force } }

Invoke-Stage5c $states5c

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
    # Stage 2b equivalents (each flag needs --smoke 3 or more so the setup chooser does not hang the run). WinForms has no flag for the formula window
    # on its own, the floating panels or the module tree; the ones below are what it can draw here.
    # The cheat sheet, batch and outliner flags draw nothing on the Riverland demo (they belong to the phase smoke test, which the demo does not pass),
    # so only the flags that do work here are run. A flag that fails is reported and skipped, it does not stop the rest.
    foreach ($flag in 'phases', 'nextphase', 'stepback', 'alerts') {
        $png = Join-Path $out "stage2b-winforms-$flag.png"
        try { Invoke-Shot $appExe (@($vmod, '--rules', $rules, "--screenshot-$flag", $png, '--smoke', '8', '--no-smoke-documents')) $env2 $png "winforms-$flag" }
        catch { Write-Warning "winforms-${flag}: $($_.Exception.Message)" }
    }
    # Stage 3a equivalent: the Ledger (--screenshot-ledger). --screenshot-outliner is NOT run: it exits 1 on Riverland (the baseline needs phases),
    # and WinForms has no flag for the tab editor, Inventory, Notes, Diagnostics, Overview or the export choice.
    $ledPng = Join-Path $out 'stage3a-winforms-ledger.png'
    try { Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot-ledger', $ledPng, '--smoke', '8', '--no-smoke-documents') $env2 $ledPng 'winforms-ledger' }
    catch { Write-Warning "winforms-ledger: $($_.Exception.Message)" }
    # Stage 4b equivalents: the Settings window per tab (--screenshot-settings --settings-tab).
    foreach ($tab in 'general', 'game', 'display', 'map', 'dice', 'toolbar', 'keyboard', 'folders') {
        $png = Join-Path $out "stage4b-winforms-settings-$tab.png"
        try { Invoke-Shot $appExe @($vmod, '--rules', $rules, '--screenshot-settings', $png, '--settings-tab', $tab, '--smoke', '8', '--no-smoke-documents') $env2 $png "winforms-settings-$tab" }
        catch { Write-Warning "winforms-settings-${tab}: $($_.Exception.Message)" }
    }
}
finally { Reset-DemoRoot }
