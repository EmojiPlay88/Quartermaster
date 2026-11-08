<#
.SYNOPSIS
  Builds the Quartermaster release downloads: the app zip (with the update helper inside) and the relay
  server archives as SEPARATE downloads. Checks every archive, writes the .sha256 files and PRINTS the
  gh command. It never creates a release, pushes or tags anything.

.PARAMETER Version
  The release version (for example 0.1.0). Must equal <Version> in Directory.Build.props.

.PARAMETER SkipTests
  Skip `dotnet test`.

.PARAMETER AllowVersionMismatch
  For the hands-on update test only (build a 0.1.1 zip from a 0.1.0 tree): do not insist that
  Directory.Build.props already says -Version. The build is still stamped with -p:Version=<Version>.

.PARAMETER OutRoot
  Where artifacts go (default <repo>\artifacts). Output: <OutRoot>\release\<Version>\ (downloads),
  <OutRoot>\work\<Version>\ (publish folders). Both are git-ignored.

Runs on Windows PowerShell 5.1 and pwsh 7.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipTests,
    [switch]$AllowVersionMismatch,
    [string]$OutRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutRoot) { $OutRoot = Join-Path $repo 'artifacts' }
$releaseDir = Join-Path $OutRoot "release\$Version"
$workDir = Join-Path $OutRoot "work\$Version"
$tag = "quartermaster-v$Version"
$appZipName = "Quartermaster-$Version-win-x64.zip"

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

$script:TextExt = @('.json', '.xml', '.txt', '.config', '.md', '.manifest', '.props', '.targets', '.yml', '.yaml', '.cmd', '.ps1', '.sh', '.html', '.css', '.js')

# Returns the list of problems found in one archive ($Kind 'zip' or 'tar.gz'); empty list = clean.
function Test-Archive([string]$Archive, [string]$Kind, [string]$Forbidden, [string[]]$Required) {
    $problems = New-Object System.Collections.Generic.List[string]
    $leaf = Split-Path -Leaf $Archive
    $user = $env:USERNAME
    $needles = New-Object System.Collections.Generic.List[string]
    foreach ($n in @('C:\Users\', 'C:\\Users\\', '.vmod', '.qrules', 'startapp.cmd')) { $needles.Add($n) }
    if ($user -and $user.Length -ge 3) { $needles.Add($user) }

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
        $binPatterns = @()
        if ($user -and $user.Length -ge 3) {
            $p = ('C:\Users\' + $user).ToLowerInvariant()
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

# ---------------------------------------------------------------------------------------------------
Write-Step "Quartermaster release $Version (tag $tag)"

# 1. Version check.
$propsPath = Join-Path $repo 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$propsVersion = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($propsVersion -ne $Version) {
    if ($AllowVersionMismatch) {
        Write-Warning "Directory.Build.props says $propsVersion but -Version is $Version (-AllowVersionMismatch: test builds only, do not release this)."
    } else {
        throw "Directory.Build.props has Version $propsVersion but -Version is $Version. Bump the props file first."
    }
}

# 2. Tests.
if ($SkipTests) { Write-Warning 'Skipping tests (-SkipTests).' }
else {
    Write-Step 'dotnet test'
    Push-Location $repo
    try { Invoke-Dotnet @('test', 'Quartermaster.slnx', '-c', 'Release') } finally { Pop-Location }
}

if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDir | Out-Null
New-Item -ItemType Directory -Path $workDir | Out-Null

# Release builds carry no debug info (DebugType=none): nothing from this machine's paths ends up in the binaries.
# PathMap also rewrites the repo folder in compiler-embedded paths (for example caller-file-path attributes).
$common = @('-c', 'Release', "-p:Version=$Version", '-p:DebugType=none', '-p:DebugSymbols=false', "-p:PathMap=$repo=.")

# 3. App (publishes Quartermaster.Updater.exe next to the app through the App project's after-publish target).
Write-Step 'Publish app (win-x64, self-contained)'
$appDir = Join-Path $workDir 'app'
Invoke-Dotnet (@('publish', (Join-Path $repo 'src\Quartermaster.App\Quartermaster.App.csproj'), '-r', 'win-x64', '--self-contained', '-o', $appDir) + $common)
foreach ($must in @('Quartermaster.App.exe', 'Quartermaster.Updater.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $must))) { throw "Publish output has no $must." }
}
foreach ($doc in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination $appDir }
$appZip = Join-Path $releaseDir $appZipName
New-ZipFromFolder $appDir $appZip

# 4. Relay, one archive per platform.
$relayArchives = @()
$tarOk = [bool](Get-Command tar -ErrorAction SilentlyContinue)
if (-not $tarOk) { Write-Warning 'tar was not found: the linux relay archives will be .zip instead of .tar.gz.' }
foreach ($t in $relayTargets) {
    $rid = $t.Rid
    Write-Step "Publish relay ($rid)"
    $dir = Join-Path $workDir "relay-$rid"
    Invoke-Dotnet (@('publish', (Join-Path $repo 'src\Quartermaster.Relay\Quartermaster.Relay.csproj'), '-r', $rid, '--self-contained', '-p:PublishSingleFile=true', '-o', $dir) + $common)
    $kind = $t.Kind
    if ($kind -eq 'tar.gz' -and -not $tarOk) { $kind = 'zip' }
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

# 5. Archive checks.
Write-Step 'Check archives'
$allProblems = New-Object System.Collections.Generic.List[string]
foreach ($p in (Test-Archive $appZip 'zip' 'Quartermaster.Relay*' @('Quartermaster.App.exe', 'Quartermaster.Updater.exe'))) { $allProblems.Add($p) }
foreach ($r in $relayArchives) {
    foreach ($p in (Test-Archive $r.Path $r.Kind 'Quartermaster.App*' @())) { $allProblems.Add($p) }
}
if ($allProblems.Count -gt 0) {
    $allProblems | ForEach-Object { Write-Host "  FAIL: $_" -ForegroundColor Red }
    throw "Archive check failed with $($allProblems.Count) problem(s). Nothing should be released."
}
Write-Host 'Archive checks passed (no local paths, user name, .vmod, .qrules or startapp.cmd; app and relay kept apart).' -ForegroundColor Green

# 6. Checksums.
Write-Step 'Checksums'
$assets = @($appZip) + @($relayArchives | ForEach-Object { $_.Path })
foreach ($a in $assets) {
    $h = Write-Sha256 $a
    $size = '{0:N1} MB' -f ((Get-Item -LiteralPath $a).Length / 1MB)
    Write-Host ("  {0}  {1}  {2}" -f $h, (Split-Path -Leaf $a), $size)
}

# 7. Release notes + the command to run later (printed, never executed).
$notes = Join-Path $releaseDir 'release-notes.md'
$notesText = @"
Quartermaster $Version

**Quartermaster-$Version-win-x64.zip** is the Windows app: unzip it anywhere and run Quartermaster.App.exe (see the README, Install / Update).
**QuartermasterRelay-$Version-<platform>** is the online-play relay server, a separate download for the person who hosts games (see docs/relay-setup.md).

The builds are not code-signed. Windows SmartScreen may warn on first start. The .sha256 files only guard against a corrupt download, they do not prove who made it.
"@
[System.IO.File]::WriteAllText($notes, $notesText.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))

$files = @($assets | ForEach-Object { $_; "$_.sha256" })
Write-Step 'Done. Files are in:'
Write-Host "  $releaseDir"
Write-Host ''
Write-Host 'NOT RUN. To publish, after the user says OK (run from the repo, with gh logged in):' -ForegroundColor Yellow
$q = ($files | ForEach-Object { '"' + $_ + '"' }) -join ' '
Write-Host "gh release create $tag $q --title `"Quartermaster $Version`" --notes-file `"$notes`" --repo EmojiPlay88/Quartermaster"
