<#
 CI-only test of the per-user setup.exe: silent install, marker kind, --version, in-place reinstall (one uninstall key),
 silent uninstall (install dir gone, user data kept), refusal of an unsafe /DIR= (non-empty foreign folder, a folder inside the data folder, a folder under Program Files),
 a stale file listed in quartermaster-files.txt is removed by the upgrade.
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
$outside = Join-Path $tmp 'outside.txt'
$subDir = Join-Path $dataDir 'qm-ci-sub'
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

    Write-Host '== refuses a non-empty folder that is not a Quartermaster install'
    $unsafeDir = Join-Path $tmp 'unsafe'
    New-Item -ItemType Directory -Force -Path $unsafeDir | Out-Null
    $unsafeFile = Join-Path $unsafeDir 'precious.txt'
    Set-Content -LiteralPath $unsafeFile -Value 'keep me' -Encoding ascii
    $p = Start-Process -FilePath $Setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$unsafeDir`"") -Wait -PassThru
    Check ($p.ExitCode -eq 7) "setup into a non-empty foreign folder should be refused with exit code 7 (PrepareToInstall), got $($p.ExitCode)"
    Check ((Test-Path -LiteralPath $unsafeFile) -and ((Get-Content -LiteralPath $unsafeFile -Raw).Trim() -eq 'keep me')) 'refused install touched the foreign folder file'
    Check (@(Get-ChildItem -LiteralPath $unsafeDir).Count -eq 1) 'refused install added files to the foreign folder'
    Check (@(UninstKeys).Count -eq 0) 'refused install left an uninstall key'

    Write-Host '== refuses a folder inside the data folder'
    $p = Start-Process -FilePath $Setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$subDir`"") -Wait -PassThru
    Check ($p.ExitCode -eq 7) "setup into a folder inside the data folder should be refused with exit code 7 (PrepareToInstall), got $($p.ExitCode)"
    Check (-not (Test-Path -LiteralPath $subDir)) "refused install created $subDir"
    Check (@(UninstKeys).Count -eq 0) 'refused install (data subfolder) left an uninstall key'

    Write-Host '== refuses a folder under Program Files'
    $pfDir = Join-Path $env:ProgramFiles 'QmCiTest'
    $p = Start-Process -FilePath $Setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$pfDir`"") -Wait -PassThru
    if ($p.ExitCode -eq 0) {
        # Should never happen: try to remove what was installed, then fail.
        $u = Get-ChildItem -LiteralPath $pfDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($u) { Start-Process -FilePath $u.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null }
    }
    Check ($p.ExitCode -eq 7) "setup into Program Files should be refused with exit code 7 (PrepareToInstall), got $($p.ExitCode)"
    Check (-not (Test-Path -LiteralPath $pfDir)) "refused install created $pfDir"
    Check (@(UninstKeys).Count -eq 0) 'refused install (Program Files) left an uninstall key'

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

    Write-Host '== a file the old version had and the new one lacks'
    $stale = Join-Path $installDir 'stale-test.dll'
    Set-Content -LiteralPath $stale -Value 'stale' -Encoding ascii
    # Hostile list lines: the uninstaller and a path outside the install folder must survive.
    Set-Content -LiteralPath $outside -Value 'outside' -Encoding ascii
    Add-Content -LiteralPath (Join-Path $installDir 'quartermaster-files.txt') -Value @('stale-test.dll', 'unins000.dat', '..\outside.txt') -Encoding ascii
    Check (Test-Path -LiteralPath $stale) 'could not create the stale test file'

    Write-Host '== reinstall over the existing install'
    Run-Setup
    $keys = @(UninstKeys)
    Check ($keys.Count -eq 1) "expected exactly one HKCU uninstall key for $appId after reinstall, found $($keys.Count)"
    Check (Test-Path -LiteralPath $exe) 'Quartermaster.exe missing after reinstall'
    Check (Test-Path -LiteralPath (Join-Path $installDir 'unins000.dat')) 'the upgrade deleted unins000.dat (the uninstaller must never be deleted)'
    Check (Test-Path -LiteralPath $outside) 'the upgrade deleted a file outside the install folder (..\outside.txt)'
    Check (-not (Test-Path -LiteralPath $stale)) 'the stale file listed in quartermaster-files.txt survived the reinstall'

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
    Remove-Item -LiteralPath $subDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $outside -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
    if ($sentinelDirCreated) { Remove-Item -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 }
