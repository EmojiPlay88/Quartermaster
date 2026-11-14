<#
.SYNOPSIS
  Self-test for build/release.ps1 -ArtifactDir (the -FromRun path minus the download). Pester-free.
  Builds a fake CI release artifact (tiny but real zip and tar.gz, dummy files for the rest, correct .sha256 files)
  in a temp folder and checks that release.ps1:
    1. succeeds on the complete set and prints the gh release create command;
    2. fails, naming the asset, when one asset is missing;
    3. fails, naming the file, when an extra file is present;
    4. fails, naming the asset, when an asset does not match its .sha256;
    5. refuses without -FromRun or -ArtifactDir, pointing at .github/workflows/release.yml.
  Exits 1 on any failure. Runs on Windows PowerShell 5.1 and pwsh 7.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseScript = Join-Path $PSScriptRoot 'release.ps1'
[xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$v = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$hostExe = (Get-Process -Id $PID).Path

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Join-Path ([IO.Path]::GetTempPath()) ("qm-release-collect-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:failures = 0

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-Sha256([string]$File) {
    $h = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8 "$File.sha256" "$h  $(Split-Path -Leaf $File)`n"
}

# A publish-like folder: the given app files, a marker and a quartermaster-files.txt listing everything.
function New-FakePublish([string]$Dir, [string[]]$Files, [string]$Kind) {
    New-Item -ItemType Directory -Path $Dir | Out-Null
    foreach ($f in $Files) { Write-Utf8 (Join-Path $Dir $f) "fake $f" }
    Write-Utf8 (Join-Path $Dir 'quartermaster-package.json') "{`"version`":`"$v`",`"package`":`"$Kind`"}"
    $list = @($Files) + @('quartermaster-package.json', 'quartermaster-files.txt')
    Write-Utf8 (Join-Path $Dir 'quartermaster-files.txt') (($list -join "`n") + "`n")
}

function New-FakeArtifact([string]$Dir) {
    New-Item -ItemType Directory -Path $Dir | Out-Null
    $stage = Join-Path $Dir '..\stage'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null

    $win = Join-Path $stage 'win'
    New-FakePublish $win @('Quartermaster.exe', 'Quartermaster.App.exe', 'Quartermaster.Updater.exe') 'win-zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($win, (Join-Path $Dir "Quartermaster-$v-win-x64.zip"))

    $lin = Join-Path $stage 'linux'
    New-FakePublish $lin @('Quartermaster', 'Quartermaster.Updater') 'linux-tar'
    # Relative paths, so no tar flavour ever sees a drive letter.
    Push-Location $stage
    try {
        & tar -czf "linux.tar.gz" -C 'linux' .
        if ($LASTEXITCODE -ne 0) { throw 'tar failed making the fake tar.gz' }
    } finally { Pop-Location }
    Move-Item -LiteralPath (Join-Path $stage 'linux.tar.gz') -Destination (Join-Path $Dir "Quartermaster-$v-linux-x64.tar.gz")

    foreach ($n in @("Quartermaster-Setup-$v.exe", "Quartermaster-$v-x86_64.AppImage", "quartermaster_${v}_amd64.deb",
                     "quartermaster-$v-1.x86_64.rpm", "Quartermaster-$v-osx-arm64.dmg", "Quartermaster-$v-osx-arm64.zip")) {
        Write-Utf8 (Join-Path $Dir $n) "fake $n"
    }
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -File)) { Write-Sha256 $f.FullName }
}

# Runs release.ps1 in a child process; returns @{ Code; Output }.
function Invoke-Release([string[]]$ReleaseArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $releaseScript @ReleaseArgs 2>&1 | Out-String
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    return @{ Code = $code; Output = $out }
}

function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if ($Ok) { Write-Host "PASS  $Name" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "FAIL  $Name" -ForegroundColor Red
        Write-Host $Detail
    }
}

function Test-Case([string]$Name, [scriptblock]$Mutate, [bool]$ExpectSuccess, [string[]]$MustContain) {
    $case = Join-Path $root ($Name -replace '[^A-Za-z0-9]', '-')
    New-Item -ItemType Directory -Path $case | Out-Null
    $art = Join-Path $case 'artifact'
    New-FakeArtifact $art
    if ($Mutate) { & $Mutate $art }
    $r = Invoke-Release @('-Version', $v, '-ArtifactDir', $art, '-SkipRelay', '-OutRoot', (Join-Path $case 'out'))
    $ok = if ($ExpectSuccess) { $r.Code -eq 0 } else { $r.Code -ne 0 }
    foreach ($m in $MustContain) { if ($r.Output.IndexOf($m, [StringComparison]::Ordinal) -lt 0) { $ok = $false } }
    Check $Name $ok ("exit code $($r.Code); expected " + $(if ($ExpectSuccess) { 'success' } else { 'failure' }) +
        " with output containing: " + ($MustContain -join ' | ') + "`n--- output ---`n" + $r.Output)
    return $case
}

try {
    Write-Host "release.ps1 collect self-test, version $v, host $hostExe"

    $case = Test-Case 'complete set succeeds' $null $true @("gh release create quartermaster-v$v", "--title `"Quartermaster $v`"", 'release-notes.md')
    $rel = Join-Path $case "out\release\$v"
    $got = @(Get-ChildItem -LiteralPath $rel -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Check 'release folder holds 8 assets + 8 .sha256 + notes' ($got.Count -eq 17 -and ($got -contains 'release-notes.md')) ("files: " + ($got -join ', '))
    $notesPath = Join-Path $rel 'release-notes.md'
    if (Test-Path -LiteralPath $notesPath) {
        $notes = [IO.File]::ReadAllText($notesPath)
        $missing = @($got | Where-Object { $_ -notlike '*.sha256' -and $_ -ne 'release-notes.md' -and $notes.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
        Check 'notes name every asset' ($missing.Count -eq 0) ("not in notes: " + ($missing -join ', '))
    } else { Check 'notes name every asset' $false 'no release-notes.md' }

    $rpm = "quartermaster-$v-1.x86_64.rpm"
    [void](Test-Case 'missing asset fails and names it' { param($a) Remove-Item -LiteralPath (Join-Path $a $rpm), (Join-Path $a "$rpm.sha256") } $false @($rpm))

    [void](Test-Case 'extra file fails and names it' { param($a) Write-Utf8 (Join-Path $a 'stray.txt') 'x' } $false @('stray.txt'))

    $dmg = "Quartermaster-$v-osx-arm64.dmg"
    [void](Test-Case 'corrupted asset fails its hash' { param($a) Write-Utf8 (Join-Path $a $dmg) 'corrupted' } $false @($dmg, 'SHA-256'))

    # The privacy checks really open the archives: a startapp.cmd inside the zip (correct .sha256) must fail.
    $zipName = "Quartermaster-$v-win-x64.zip"
    [void](Test-Case 'startapp.cmd in the zip fails the privacy check' {
        param($a)
        $z = [IO.Compression.ZipFile]::Open((Join-Path $a $zipName), [IO.Compression.ZipArchiveMode]::Update)
        try {
            $w = New-Object IO.StreamWriter($z.CreateEntry('startapp.cmd').Open())
            try { $w.Write('@echo off') } finally { $w.Dispose() }
        } finally { $z.Dispose() }
        Write-Sha256 (Join-Path $a $zipName)
    } $false @("$zipName entry name 'startapp.cmd'"))

    $r = Invoke-Release @('-Version', $v, '-SkipRelay', '-OutRoot', (Join-Path $root 'none'))
    Check 'no -FromRun/-ArtifactDir refuses, pointing at release.yml' ($r.Code -ne 0 -and $r.Output.Contains('.github/workflows/release.yml')) ("exit $($r.Code)`n" + $r.Output)
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) { Write-Host "$($script:failures) check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host 'All release collect checks PASSED' -ForegroundColor Green
exit 0
