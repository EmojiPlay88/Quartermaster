<#
 CI-only test of the per-user setup.exe: silent install, marker kind, --version, in-place reinstall (one uninstall key),
 silent uninstall (install dir gone, user data kept).
 It writes a sentinel into the real data folder (%LOCALAPPDATA%\Quartermaster), so it REFUSES to run unless $env:CI is 'true'.
 Usage: build/windows/test-setup.ps1 -Setup <Quartermaster-Setup-x.y.z.exe> -Version <x.y.z>
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true') {
    [Console]::Error.WriteLine('test-setup: refusing to run outside CI (it writes into %LOCALAPPDATA%\Quartermaster). Set CI=true only on a throwaway machine.')
    exit 1
}

$appId = '{6F0B6C2E-9C1F-4E8E-9C55-5C5E0A1D2B7E}'
$uninstRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$Setup = (Resolve-Path -LiteralPath $Setup).Path
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("qm-setuptest-" + [Guid]::NewGuid().ToString('N'))
$installDir = Join-Path $tmp 'install'
$dataDir = Join-Path $env:LOCALAPPDATA 'Quartermaster'
$sentinel = Join-Path $dataDir 'ci-sentinel.txt'
$sentinelDirCreated = $false
$failed = $false

function Check([bool]$ok, [string]$msg) { if (-not $ok) { throw "ASSERTION FAILED: $msg" } }
# PSChildName holds the literal key name; -like would treat the braces fine but [] none here.
function UninstKeys { @(Get-ChildItem -LiteralPath $uninstRoot -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "$appId*" }) }
function Run-Setup {
    $p = Start-Process -FilePath $Setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$installDir`"") -Wait -PassThru
    Check ($p.ExitCode -eq 0) "setup exited with $($p.ExitCode)"
}

try {
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    Check (@(UninstKeys).Count -eq 0) "an uninstall key for $appId already exists before the test"

    Write-Host '== silent install'
    Run-Setup
    $exe = Join-Path $installDir 'Quartermaster.exe'
    Check (Test-Path -LiteralPath $exe) "Quartermaster.exe missing in $installDir"

    Write-Host '== marker kind'
    $marker = Get-Content -LiteralPath (Join-Path $installDir 'quartermaster-package.json') -Raw | ConvertFrom-Json
    Check ($marker.package -eq 'win-setup') "marker kind is '$($marker.package)', expected win-setup"
    Check ($marker.version -eq $Version) "marker version is '$($marker.version)', expected $Version"

    Write-Host '== --version'
    $outFile = Join-Path $tmp 'version.txt'
    $p = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru -RedirectStandardOutput $outFile
    Check ($p.ExitCode -eq 0) "--version exited with $($p.ExitCode)"
    $text = (Get-Content -LiteralPath $outFile -Raw).Trim()
    Check ($text -eq "Quartermaster $Version") "--version printed '$text', expected 'Quartermaster $Version'"

    Write-Host '== sentinel in the data folder'
    if (-not (Test-Path -LiteralPath $dataDir)) { $sentinelDirCreated = $true; New-Item -ItemType Directory -Force -Path $dataDir | Out-Null }
    Set-Content -LiteralPath $sentinel -Value 'ci sentinel' -Encoding ascii
    Check (Test-Path -LiteralPath $sentinel) 'could not write the sentinel'

    Write-Host '== reinstall over the existing install'
    Run-Setup
    $keys = @(UninstKeys)
    Check ($keys.Count -eq 1) "expected exactly one HKCU uninstall key for $appId after reinstall, found $($keys.Count)"
    Check (Test-Path -LiteralPath $exe) 'Quartermaster.exe missing after reinstall'

    Write-Host '== silent uninstall'
    $unins = Get-ChildItem -LiteralPath $installDir -Filter 'unins*.exe' | Select-Object -First 1
    Check ($null -ne $unins) "no uninstaller (unins*.exe) in $installDir"
    $p = Start-Process -FilePath $unins.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    Check ($p.ExitCode -eq 0) "uninstaller exited with $($p.ExitCode)"
    # The uninstaller hands off to a temp copy, so the first process can exit before the folder is gone.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-Path -LiteralPath $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Check (-not (Test-Path -LiteralPath $installDir)) "install dir $installDir still exists after uninstall"
    Check (@(UninstKeys).Count -eq 0) 'uninstall key still present after uninstall'
    Check (Test-Path -LiteralPath $sentinel) 'uninstall deleted the data-folder sentinel (user data must survive)'

    Write-Host 'test-setup: all checks passed'
} catch {
    [Console]::Error.WriteLine("test-setup: $($_.Exception.Message)")
    $failed = $true
} finally {
    Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
    if ($sentinelDirCreated) { Remove-Item -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 }
