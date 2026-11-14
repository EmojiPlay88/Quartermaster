<#
 Rewrites <Folder>\quartermaster-package.json to {"version":"<same>","package":"<Kind>"},
 the same shape the Desktop csproj writes. Refuses unknown kinds and a missing/unparseable marker.
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory)][string]$Kind
)
$ErrorActionPreference = 'Stop'
$kinds = 'win-zip', 'win-setup', 'mac-app', 'linux-tar', 'linux-appimage', 'linux-deb', 'linux-rpm'
if ($kinds -cnotcontains $Kind) {
    [Console]::Error.WriteLine("Set-PackageKind: unknown kind '$Kind' (valid: $($kinds -join ' '))"); exit 1
}
$marker = Join-Path $Folder 'quartermaster-package.json'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
    [Console]::Error.WriteLine("Set-PackageKind: no quartermaster-package.json in '$Folder'"); exit 1
}
try {
    $version = (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).version
    if ([string]::IsNullOrEmpty($version) -or $version -isnot [string]) { throw 'no version' }
} catch {
    [Console]::Error.WriteLine("Set-PackageKind: '$marker' is not a valid package marker"); exit 1
}
$json = '{"version":' + ($version | ConvertTo-Json -Compress) + ',"package":"' + $Kind + '"}' + "`n"
[IO.File]::WriteAllText((Resolve-Path -LiteralPath $marker).Path, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "package kind set to $Kind"
