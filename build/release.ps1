<#
.SYNOPSIS
  Collects a Quartermaster release from CI: takes the combined artifact Quartermaster-release-<v> of a
  .github/workflows/release.yml run (8 app packages built and tested on Windows, macOS and Linux, each with a
  .sha256), checks the exact asset list and every checksum, re-runs the archive privacy checks on the Windows zip
  and the Linux tar.gz, builds the relay server archives as SEPARATE downloads, writes the release notes and
  PRINTS the gh command. It never creates a release, pushes or tags anything.

.PARAMETER Version
  The release version (for example 0.1.0). Must equal <Version> in Directory.Build.props.

.PARAMETER FromRun
  The id of a successful release.yml run; its artifact Quartermaster-release-<Version> is downloaded with gh.

.PARAMETER ArtifactDir
  A folder that already holds the artifact's files (skips the download; used by build/test-release-collect.ps1).

.PARAMETER SkipRelay
  Do not build the relay archives (tests only; a real release needs them).

.PARAMETER OutRoot
  Where artifacts go (default <repo>\artifacts). Output: <OutRoot>\release\<Version>\ (downloads),
  <OutRoot>\work\<Version>\ (relay publish folders, the downloaded artifact). Both are git-ignored.

Runs on Windows PowerShell 5.1 and pwsh 7.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$FromRun,
    [string]$ArtifactDir,
    [switch]$SkipRelay,
    [string]$OutRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoSlug = 'EmojiPlay88/Quartermaster'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "-Version must be a plain semantic version x.y.z (for example 0.1.0), not '$Version'." }
if (-not $FromRun -and -not $ArtifactDir) {
    # Also on stdout: error records can be wrapped mid-word when redirected.
    $msg = "release.ps1 no longer builds the app itself. Run the release workflow (.github/workflows/release.yml: Actions > release > Run workflow, version $Version), then pass its run id: build/release.ps1 -Version $Version -FromRun <run id>"
    Write-Host $msg -ForegroundColor Red
    throw $msg
}
if ($FromRun -and $ArtifactDir) { throw 'Pass either -FromRun or -ArtifactDir, not both.' }
if ($FromRun -and $FromRun -notmatch '^\d+$') { throw "-FromRun must be a numeric workflow run id, not '$FromRun'." }
# The Linux tar.gz is checked with tar, and the linux relay downloads must be .tar.gz (their names are fixed).
if (-not (Get-Command tar -ErrorAction SilentlyContinue)) { throw 'tar was not found on PATH (Windows 10 1803+ ships it as tar.exe); it is needed for the .tar.gz checks and the linux relay downloads.' }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutRoot) { $OutRoot = Join-Path $repo 'artifacts' }
$releaseDir = Join-Path $OutRoot "release\$Version"
$workDir = Join-Path $OutRoot "work\$Version"
$tag = "quartermaster-v$Version"
$artifactName = "Quartermaster-release-$Version"

# The 8 app assets (names fixed by the 6a updater), each with its one plain release-notes line.
$appAssets = [ordered]@{
    "Quartermaster-Setup-$Version.exe"        = 'Windows installer (per-user, no admin rights needed).'
    "Quartermaster-$Version-win-x64.zip"      = 'Windows portable zip: unzip anywhere and run Quartermaster.exe.'
    "Quartermaster-$Version-osx-arm64.dmg"    = 'macOS (Apple silicon) disk image: drag Quartermaster to Applications.'
    "Quartermaster-$Version-osx-arm64.zip"    = 'macOS (Apple silicon) app as a zip (also used by the in-app updater).'
    "Quartermaster-$Version-x86_64.AppImage"  = 'Linux AppImage (x86_64): make it executable and run it.'
    "quartermaster_${Version}_amd64.deb"      = 'Linux .deb package for Debian and Ubuntu (amd64).'
    "quartermaster-$Version-1.x86_64.rpm"     = 'Linux .rpm package for Fedora and similar (x86_64).'
    "Quartermaster-$Version-linux-x64.tar.gz" = 'Linux portable tar.gz (x64): extract anywhere and run Quartermaster.'
}

# Relay targets: rid -> archive kind.
$relayTargets = @(
    @{ Rid = 'win-x64';     Kind = 'zip' },
    @{ Rid = 'osx-arm64';   Kind = 'zip' },
    @{ Rid = 'linux-x64';   Kind = 'tar.gz' },
    @{ Rid = 'linux-arm64'; Kind = 'tar.gz' }
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Streaming, case-insensitive (ASCII) byte search, so large binaries are never loaded whole.
if (-not ('QmBinScan' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class QmBinScan
{
    static byte Low(byte b) { return (b >= 65 && b <= 90) ? (byte)(b + 32) : b; }
    // pattern must be given in lower case.
    public static bool Contains(string path, byte[] pattern)
    {
        int n = pattern.Length;
        byte[] buf = new byte[1 << 20];
        int carry = 0;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while ((read = fs.Read(buf, carry, buf.Length - carry)) > 0)
            {
                int total = carry + read;
                for (int i = 0; i + n <= total; i++)
                {
                    if (Low(buf[i]) != pattern[0]) continue;
                    int j = 1;
                    while (j < n && Low(buf[i + j]) == pattern[j]) j++;
                    if (j == n) return true;
                }
                carry = Math.Min(n - 1, total);
                Buffer.BlockCopy(buf, total - carry, buf, 0, carry);
            }
        }
        return false;
    }
}
'@
}

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]]$DotnetArgs) {
    Write-Host ("dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor DarkGray
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs[0]) failed (exit code $LASTEXITCODE)." }
}

function New-ZipFromFolder([string]$Folder, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $root = (Resolve-Path -LiteralPath $Folder).Path.TrimEnd('\', '/')
    $fs = [System.IO.File]::Create($ZipPath)
    try {
        $za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $files = Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName
            foreach ($f in $files) {
                $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }
}

function Write-Sha256([string]$File) {
    $h = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $File
    # Exactly "<hex>  <file name>" (two spaces) plus a line feed, no BOM.
    [System.IO.File]::WriteAllText("$File.sha256", "$h  $name`n", (New-Object System.Text.UTF8Encoding($false)))
    return $h
}

# The user name and the user-profile folder name (they can differ), each only when long enough to be a meaningful needle.
$script:UserNames = New-Object System.Collections.Generic.List[string]
foreach ($u in @($env:USERNAME, $(if ($env:USERPROFILE) { Split-Path -Leaf $env:USERPROFILE } else { $null }))) {
    if ($u -and $u.Length -ge 3 -and -not ($script:UserNames -contains $u)) { $script:UserNames.Add($u) }
}

$script:TextExt = @('.json', '.xml', '.txt', '.config', '.md', '.manifest', '.props', '.targets', '.yml', '.yaml', '.cmd', '.ps1', '.sh', '.html', '.css', '.js')

# -CheckManifest: quartermaster-files.txt must list exactly the archive's files (the updater only moves listed files).
# Returns the list of problems found in one archive ($Kind 'zip' or 'tar.gz'); empty list = clean.
function Test-Archive([string]$Archive, [string]$Kind, [string]$Forbidden, [string[]]$Required, [switch]$CheckManifest) {
    $problems = New-Object System.Collections.Generic.List[string]
    $leaf = Split-Path -Leaf $Archive
    $needles = New-Object System.Collections.Generic.List[string]
    foreach ($n in @('C:\Users\', 'C:\\Users\\', '.vmod', '.qrules', 'startapp.cmd')) { $needles.Add($n) }
    foreach ($u in $script:UserNames) { $needles.Add($u) }

    # 1. Entry names.
    $names = @()
    if ($Kind -eq 'zip') {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
        try { $names = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
    } else {
        $names = @(& tar -tzf $Archive)
        if ($LASTEXITCODE -ne 0) { throw "tar could not list $leaf." }
    }
    foreach ($e in $names) {
        foreach ($n in $needles) {
            if ($e.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("$leaf entry name '$e' contains '$n'") }
        }
        $base = ($e.TrimEnd('/') -split '/')[-1]
        if ($Forbidden -and $base -like $Forbidden) { $problems.Add("$leaf contains forbidden file '$e' (matches $Forbidden)") }
    }
    foreach ($r in $Required) {
        $found = $names | Where-Object { ($_ -replace '^\./', '') -eq $r }
        if (-not $found) { $problems.Add("$leaf is missing required entry '$r'") }
    }

    # 2. Contents: extract to a temp folder and scan.
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("qm-relcheck-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        if ($Kind -eq 'zip') { [System.IO.Compression.ZipFile]::ExtractToDirectory($Archive, $tmp) }
        else {
            & tar -xzf $Archive -C $tmp
            if ($LASTEXITCODE -ne 0) { throw "tar could not extract $leaf." }
        }
        if ($CheckManifest) {
            $files = @(Get-ChildItem -LiteralPath $tmp -Recurse -File | ForEach-Object { $_.FullName.Substring($tmp.Length + 1).Replace('\', '/') })
            $listFile = Join-Path $tmp 'quartermaster-files.txt'
            if (-not (Test-Path -LiteralPath $listFile)) { $problems.Add("$leaf has no quartermaster-files.txt") }
            else {
                $listed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($line in ([System.IO.File]::ReadAllText($listFile) -split "`r?`n")) { $l = $line.Trim(); if ($l) { [void]$listed.Add($l) } }
                $inArchive = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($f in $files) {
                    [void]$inArchive.Add($f)
                    if (-not $listed.Contains($f)) { $problems.Add("$leaf entry '$f' is not in quartermaster-files.txt") }
                }
                foreach ($l in $listed) { if (-not $inArchive.Contains($l)) { $problems.Add("$leaf quartermaster-files.txt lists '$l', which is not in the archive") } }
            }
        }
        $binPatterns = @()
        foreach ($u in $script:UserNames) {
            $p = ('C:\Users\' + $u).ToLowerInvariant()
            $binPatterns += , ([System.Text.Encoding]::UTF8.GetBytes($p))
            $binPatterns += , ([System.Text.Encoding]::Unicode.GetBytes($p))
        }
        foreach ($f in (Get-ChildItem -LiteralPath $tmp -Recurse -File)) {
            $ext = $f.Extension.ToLowerInvariant()
            if ($ext -eq '.pdb') { continue }
            $rel = $f.FullName.Substring($tmp.Length + 1)
            if ($script:TextExt -contains $ext) {
                $text = [System.IO.File]::ReadAllText($f.FullName)
                foreach ($n in $needles) {
                    if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("$leaf file '$rel' contains '$n'") }
                }
            } else {
                foreach ($pat in $binPatterns) {
                    if ([QmBinScan]::Contains($f.FullName, [byte[]]$pat)) {
                        $problems.Add("$leaf binary '$rel' embeds a local path (C:\Users\<user name>)")
                        break
                    }
                }
            }
        }
    } finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    return , $problems
}

# Throws when $Problems is not empty, after listing each one on its own line (Write-Host, so long names are never wrapped).
function Assert-NoProblems($Problems, [string]$What) {
    if ($Problems.Count -eq 0) { return }
    $Problems | ForEach-Object { Write-Host "  FAIL: $_" -ForegroundColor Red }
    throw "$What failed with $($Problems.Count) problem(s). Nothing should be released."
}

# ---------------------------------------------------------------------------------------------------
Write-Step "Quartermaster release $Version (tag $tag)"

# 1. Version check.
$propsPath = Join-Path $repo 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$propsVersion = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($propsVersion -ne $Version) { throw "Directory.Build.props has Version $propsVersion but -Version is $Version. Bump the props file first." }

if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDir | Out-Null
New-Item -ItemType Directory -Path $workDir | Out-Null

# 2. The CI artifact.
if ($FromRun) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) was not found on PATH; it is needed for -FromRun.' }
    $ArtifactDir = Join-Path $workDir 'ci-artifact'
    Write-Step "Download $artifactName from run $FromRun"
    & gh run download $FromRun -n $artifactName -D $ArtifactDir --repo $repoSlug
    if ($LASTEXITCODE -ne 0) { throw "gh run download $FromRun -n $artifactName failed (exit code $LASTEXITCODE). Did that release.yml run finish green?" }
}
if (-not (Test-Path -LiteralPath $ArtifactDir -PathType Container)) { throw "Artifact folder '$ArtifactDir' does not exist." }
$ArtifactDir = (Resolve-Path -LiteralPath $ArtifactDir).Path.TrimEnd('\', '/')

# 3. Exact file list: the 8 assets and their .sha256; nothing missing, nothing extra (sub-folders count as extra).
Write-Step 'Check asset list and checksums'
$expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($n in $appAssets.Keys) { [void]$expected.Add($n); [void]$expected.Add("$n.sha256") }
$present = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($i in @(Get-ChildItem -LiteralPath $ArtifactDir -Recurse -Force)) {
    $rel = $i.FullName.Substring($ArtifactDir.Length + 1).Replace('\', '/')
    if ($i.PSIsContainer) { $rel += '/' }
    [void]$present.Add($rel)
}
$listProblems = New-Object System.Collections.Generic.List[string]
foreach ($n in ($expected | Sort-Object)) { if (-not $present.Contains($n)) { $listProblems.Add("missing from the artifact: $n") } }
foreach ($n in ($present | Sort-Object)) { if (-not $expected.Contains($n)) { $listProblems.Add("unexpected file in the artifact: $n") } }
Assert-NoProblems $listProblems 'Asset list check'

# 4. Every .sha256 is exactly "<64 lower-case hex>  <asset name>" (one line feed allowed) and matches its asset.
$hashProblems = New-Object System.Collections.Generic.List[string]
$hashes = @{}
foreach ($n in $appAssets.Keys) {
    $text = [System.IO.File]::ReadAllText((Join-Path $ArtifactDir "$n.sha256"))
    $m = [regex]::Match($text, '\A([0-9a-f]{64})  ([^\r\n]+)\n?\z')
    if (-not $m.Success) { $hashProblems.Add("$n.sha256 is not in the form '<64 lower-case hex>  <name>'"); continue }
    if ($m.Groups[2].Value -cne $n) { $hashProblems.Add("$n.sha256 names '$($m.Groups[2].Value)' instead of $n"); continue }
    $actual = (Get-FileHash -LiteralPath (Join-Path $ArtifactDir $n) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $m.Groups[1].Value) { $hashProblems.Add("$n does not match its SHA-256 file (file $actual, .sha256 $($m.Groups[1].Value))"); continue }
    $hashes[$n] = $actual
}
Assert-NoProblems $hashProblems 'Checksum check'
foreach ($n in $appAssets.Keys) {
    Copy-Item -LiteralPath (Join-Path $ArtifactDir $n), (Join-Path $ArtifactDir "$n.sha256") -Destination $releaseDir
}
Write-Host "All $($appAssets.Count) assets present, nothing extra, every .sha256 matches." -ForegroundColor Green

# Release builds carry no debug info (DebugType=none): nothing from this machine's paths ends up in the binaries.
# PathMap also rewrites the repo folder in compiler-embedded paths (for example caller-file-path attributes).
$common = @('-c', 'Release', "-p:Version=$Version", '-p:DebugType=none', '-p:DebugSymbols=false', "-p:PathMap=$repo=.")

# 5. Relay, one archive per platform.
$relayArchives = @()
if ($SkipRelay) { Write-Warning 'Skipping the relay archives (-SkipRelay): tests only, do not release this.' }
else {
    foreach ($t in $relayTargets) {
        $rid = $t.Rid
        Write-Step "Publish relay ($rid)"
        $dir = Join-Path $workDir "relay-$rid"
        Invoke-Dotnet (@('publish', (Join-Path $repo 'src\Quartermaster.Relay\Quartermaster.Relay.csproj'), '-r', $rid, '--self-contained', '-p:PublishSingleFile=true', '-o', $dir) + $common)
        $kind = $t.Kind
        $name = "QuartermasterRelay-$Version-$rid.$kind"
        $out = Join-Path $releaseDir $name
        if ($kind -eq 'zip') { New-ZipFromFolder $dir $out }
        else {
            # Relative paths from the work folder, so tar never sees a drive letter.
            Push-Location $workDir
            try {
                & tar -czf "../../release/$Version/$name" -C "relay-$rid" .
                if ($LASTEXITCODE -ne 0) { throw "tar failed for $name." }
            } finally { Pop-Location }
        }
        $relayArchives += @{ Path = $out; Kind = $kind }
    }
}

# 6. Archive checks: the app zip and tar.gz (the archives the updater unpacks) and every relay archive.
Write-Step 'Check archives'
$allProblems = New-Object System.Collections.Generic.List[string]
$winZip = Join-Path $releaseDir "Quartermaster-$Version-win-x64.zip"
$linuxTar = Join-Path $releaseDir "Quartermaster-$Version-linux-x64.tar.gz"
foreach ($p in (Test-Archive $winZip 'zip' 'Quartermaster.Relay*' @('Quartermaster.exe', 'Quartermaster.App.exe', 'Quartermaster.Updater.exe', 'quartermaster-package.json', 'quartermaster-files.txt') -CheckManifest)) { $allProblems.Add($p) }
foreach ($p in (Test-Archive $linuxTar 'tar.gz' 'Quartermaster.Relay*' @('Quartermaster', 'Quartermaster.Updater', 'quartermaster-package.json', 'quartermaster-files.txt') -CheckManifest)) { $allProblems.Add($p) }
foreach ($r in $relayArchives) {
    foreach ($p in (Test-Archive $r.Path $r.Kind 'Quartermaster.App*' @())) { $allProblems.Add($p) }
}
Assert-NoProblems $allProblems 'Archive check'
Write-Host 'Archive checks passed (no local paths, user or profile name, .vmod, .qrules or startapp.cmd; app and relay kept apart; quartermaster-files.txt lists exactly the zip and the tar.gz).' -ForegroundColor Green

# 7. Checksums of the relay archives (the app assets keep the .sha256 files CI wrote and this script verified).
Write-Step 'Checksums'
foreach ($r in $relayArchives) { $hashes[(Split-Path -Leaf $r.Path)] = Write-Sha256 $r.Path }
$assets = @($appAssets.Keys | ForEach-Object { Join-Path $releaseDir $_ }) + @($relayArchives | ForEach-Object { $_.Path })
foreach ($a in $assets) {
    $leaf = Split-Path -Leaf $a
    $size = '{0:N1} MB' -f ((Get-Item -LiteralPath $a).Length / 1MB)
    Write-Host ("  {0}  {1}  {2}" -f $hashes[$leaf], $leaf, $size)
}

# 8. Release notes (one plain line per asset) + the command to run later (printed, never executed).
$notes = Join-Path $releaseDir 'release-notes.md'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Quartermaster $Version")
$lines.Add('')
foreach ($n in $appAssets.Keys) { $lines.Add("$n - $($appAssets[$n])") }
foreach ($r in $relayArchives) {
    $lines.Add("$(Split-Path -Leaf $r.Path) - online-play relay server, a separate download for the person who hosts games (see https://github.com/$repoSlug/wiki/Hosting-a-Relay).")
}
$lines.Add('')
$lines.Add('The builds are not code-signed: Windows SmartScreen and macOS Gatekeeper may warn on first start (see the README, Install). The .sha256 files only guard against a corrupt download, they do not prove who made it.')
[System.IO.File]::WriteAllText($notes, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$files = @($assets | ForEach-Object { $_; "$_.sha256" })
Write-Step 'Done. Files are in:'
Write-Host "  $releaseDir"
Write-Host ''
Write-Host 'NOT RUN. To publish, after the user says OK (run from the repo, with gh logged in):' -ForegroundColor Yellow
$q = ($files | ForEach-Object { '"' + $_ + '"' }) -join ' '
Write-Host "gh release create $tag $q --title `"Quartermaster $Version`" --notes-file `"$notes`" --repo $repoSlug"
