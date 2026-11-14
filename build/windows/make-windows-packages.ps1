<#
 Builds the per-user setup.exe (Inno Setup 6) and the portable zip from a win-x64 publish folder, plus .sha256 files.
 The input publish folder is never modified: each package gets its own staged copy with its own package kind.
 Usage: build/windows/make-windows-packages.ps1 -Publish <dir> -Version <x.y.z> -Out <dir> [-SkipSetup]
 -SkipSetup builds only the zip (for machines without Inno Setup); CI never uses it.
#>
param(
    [Parameter(Mandatory)][string]$Publish,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [switch]$SkipSetup
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Pinned Inno Setup release (>= 2 weeks old); choco verifies the package checksum.
$InnoVersion = '6.5.4'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$setKind = Join-Path $here '..\Set-PackageKind.ps1'

function Fail([string]$m) { [Console]::Error.WriteLine("make-windows-packages: $m"); exit 1 }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version '$Version' is not x.y.z" }
if (-not (Test-Path -LiteralPath (Join-Path $Publish 'Quartermaster.exe'))) { Fail "no Quartermaster.exe in '$Publish'" }
if (-not (Test-Path -LiteralPath (Join-Path $Publish 'quartermaster-package.json'))) { Fail "no quartermaster-package.json in '$Publish'" }
$markerVersion = (Get-Content -LiteralPath (Join-Path $Publish 'quartermaster-package.json') -Raw | ConvertFrom-Json).version
if ($markerVersion -ne $Version) { Fail "publish marker says $markerVersion, expected $Version" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$Publish = (Resolve-Path -LiteralPath $Publish).Path

$setupName = "Quartermaster-Setup-$Version.exe"
$zipName = "Quartermaster-$Version-win-x64.zip"
foreach ($n in $setupName, $zipName) {
    Remove-Item -LiteralPath (Join-Path $Out $n), (Join-Path $Out "$n.sha256") -Force -ErrorAction SilentlyContinue
}

# Same shape as New-ZipFromFolder in build/release.ps1: forward-slash entry names, sorted, files at the top.
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
    # Exactly "<hex>  <file name>" (two spaces) plus a line feed, no BOM (same as the macOS scripts).
    [System.IO.File]::WriteAllText("$File.sha256", "$h  $(Split-Path -Leaf $File)`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Find-Iscc {
    $c = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $p = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    if (Test-Path -LiteralPath $p) { return $p }
    return $null
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("qm-winpkg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # Zip: staged copy with kind win-zip.
    $zipStage = Join-Path $work 'zip'
    Copy-Item -LiteralPath $Publish -Destination $zipStage -Recurse
    $global:LASTEXITCODE = 0
    & $setKind -Folder $zipStage -Kind 'win-zip'
    if ($LASTEXITCODE -ne 0) { Fail 'Set-PackageKind failed for win-zip' }
    New-ZipFromFolder $zipStage (Join-Path $Out $zipName)
    Write-Sha256 (Join-Path $Out $zipName)
    Write-Host "built $zipName"

    if ($SkipSetup) { Write-Host 'skipped setup (-SkipSetup)' }
    else {
        # Setup: separate staged copy with kind win-setup.
        $setupStage = Join-Path $work 'setup'
        Copy-Item -LiteralPath $Publish -Destination $setupStage -Recurse
        $global:LASTEXITCODE = 0
        & $setKind -Folder $setupStage -Kind 'win-setup'
        if ($LASTEXITCODE -ne 0) { Fail 'Set-PackageKind failed for win-setup' }

        $iscc = Find-Iscc
        if (-not $iscc) {
            Write-Host "Inno Setup not found; installing $InnoVersion with choco"
            & choco install innosetup --version $InnoVersion -y --no-progress
            if ($LASTEXITCODE -ne 0) { Fail "choco install innosetup $InnoVersion failed" }
            $iscc = Find-Iscc
            if (-not $iscc) { Fail 'ISCC.exe not found after installing Inno Setup' }
        }
        & $iscc "/DAppVersion=$Version" "/DSourceDir=$setupStage" "/DOutputDir=$Out" /Qp (Join-Path $here 'quartermaster.iss')
        if ($LASTEXITCODE -ne 0) { Fail "ISCC failed ($LASTEXITCODE)" }
        if (-not (Test-Path -LiteralPath (Join-Path $Out $setupName))) { Fail "ISCC produced no $setupName" }
        Write-Sha256 (Join-Path $Out $setupName)
        Write-Host "built $setupName"
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
