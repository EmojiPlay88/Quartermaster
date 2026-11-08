<#
.SYNOPSIS
  Publishes docs/wiki/ to the GitHub wiki repository.
.DESCRIPTION
  Clones the wiki repo into a temp folder, mirrors docs/wiki/ into it (the demo/ folder
  stays in the main repo and is NOT copied), lists added/changed/deleted files, then
  commits and pushes. With -DryRun nothing is committed or pushed.
  Exit codes: 0 ok, 2 wiki is empty (never initialised), 3 deletions need -AllowDelete.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$AllowDelete,
    [string]$Remote = 'https://github.com/EmojiPlay88/Quartermaster.wiki.git'
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'docs\wiki'
if (-not (Test-Path $source)) { Write-Host "Source folder not found: $source"; exit 1 }

$work = Join-Path ([IO.Path]::GetTempPath()) ('qm-wiki-' + [guid]::NewGuid().ToString('N'))
try {
    $cloneOut = & git clone --quiet $Remote $work 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        if ($cloneOut -match 'not found' -or $cloneOut -match 'does not exist') {
            Write-Host 'The wiki is empty. Open https://github.com/EmojiPlay88/Quartermaster/wiki, click Create the first page, save, then run again.'
            exit 2
        }
        Write-Host "git clone failed:`n$cloneOut"
        exit 1
    }

    # Mirror: remove everything except .git, then copy docs/wiki without demo/.
    Get-ChildItem -LiteralPath $work -Force | Where-Object { $_.Name -ne '.git' } |
        Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -ne 'demo' } |
        Copy-Item -Destination $work -Recurse -Force

    Push-Location $work
    try {
        & git add -A | Out-Null
        $status = @(& git status --porcelain)
        if ($status.Count -eq 0) { Write-Host 'Wiki is already up to date.'; exit 0 }

        $deleted = @($status | Where-Object { $_ -match '^\s*D' })
        Write-Host 'Changes to publish:'
        foreach ($line in $status) {
            $code = $line.Substring(0, 2).Trim()
            $name = $line.Substring(3)
            $label = switch -Regex ($code) { '^A' { 'added  ' } '^D' { 'deleted' } default { 'changed' } }
            Write-Host "  $label $name"
        }

        if ($deleted.Count -gt 0 -and -not $AllowDelete) {
            Write-Host "This would delete $($deleted.Count) wiki file(s). Re-run with -AllowDelete if that is intended."
            exit 3
        }

        if ($DryRun) {
            Write-Host ''
            & git diff --cached --stat
            Write-Host 'Dry run: nothing committed or pushed.'
            exit 0
        }

        & git commit --quiet -m 'Update wiki from docs/wiki' | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host 'git commit failed.'; exit 1 }
        & git push --quiet
        if ($LASTEXITCODE -ne 0) { Write-Host 'git push failed.'; exit 1 }
        Write-Host 'Wiki published.'
        exit 0
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
