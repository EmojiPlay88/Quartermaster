<#
.SYNOPSIS
  Publishes docs/wiki/ to the GitHub wiki repository.
.DESCRIPTION
  Clones the wiki repo into a temp folder, mirrors docs/wiki/ into it (the demo/ folder
  stays in the main repo and is NOT copied), lists added/changed/deleted files, then
  commits and pushes. With -DryRun nothing is committed or pushed.
  The commit uses user.name / user.email from this repo's git config; the email must be
  a GitHub noreply address.
  Exit codes: 0 ok, 1 error, 2 wiki is empty (never initialised), 3 deletions need -AllowDelete.
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

$emptyMessage = 'The wiki is empty. Open https://github.com/EmojiPlay88/Quartermaster/wiki, click Create the first page, save, then run again.'

# Commit identity comes from the main repo; refuse anything that is not a noreply address.
$userName = ((& git -C $repoRoot config user.name) | Out-String).Trim()
$userEmail = ((& git -C $repoRoot config user.email) | Out-String).Trim()
if (-not $userName -or -not $userEmail) {
    Write-Host 'git user.name / user.email are not configured in this repository.'
    exit 1
}
if (-not $userEmail.EndsWith('@users.noreply.github.com')) {
    Write-Host "Refusing to publish: git user.email must end in @users.noreply.github.com (found a different address)."
    exit 1
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('qm-wiki-' + [guid]::NewGuid().ToString('N'))
try {
    $cloneLines = & git -c core.autocrlf=false clone --quiet $Remote $work 2>&1 | ForEach-Object { $_.ToString() } | Where-Object { $_ -notmatch 'RemoteException' }
    $cloneOut = ($cloneLines | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        if ($cloneOut -match 'not found' -or $cloneOut -match 'does not exist' -or $cloneOut -match 'does not appear to be a git repository') {
            Write-Host $emptyMessage
            exit 2
        }
        Write-Host "git clone failed:"
        Write-Host $cloneOut
        exit 1
    }

    Push-Location $work
    try {
        & git config core.autocrlf false
        & git rev-parse --verify --quiet HEAD 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host $emptyMessage; exit 2 }

        # Mirror: remove everything except .git, then copy docs/wiki without demo/.
        Get-ChildItem -LiteralPath $work -Force | Where-Object { $_.Name -ne '.git' } |
            Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -ne 'demo' } |
            Copy-Item -Destination $work -Recurse -Force

        & git add -A | Out-Null
        $raw = (& git diff --cached --name-status --no-renames -z | Out-String)
        $parts = @($raw -split "`0" | Where-Object { $_ -ne '' })
        if ($parts.Count -eq 0) { Write-Host 'Wiki is already up to date.'; exit 0 }

        $deleted = 0
        Write-Host 'Changes to publish:'
        for ($i = 0; $i + 1 -lt $parts.Count; $i += 2) {
            $code = $parts[$i].Trim()
            $name = $parts[$i + 1].Trim()
            $label = switch ($code) { 'A' { 'added  ' } 'D' { 'deleted'; $deleted++ } default { 'changed' } }
            Write-Host "  $label $name"
        }
        Write-Host ''
        & git -c core.quotepath=false diff --cached --stat

        if ($deleted -gt 0 -and -not $AllowDelete) {
            Write-Host "This would delete $deleted wiki file(s). Re-run with -AllowDelete if that is intended."
            exit 3
        }

        if ($DryRun) {
            Write-Host 'Dry run: nothing committed or pushed.'
            exit 0
        }

        & git -c "user.name=$userName" -c "user.email=$userEmail" commit --quiet -m 'Update wiki from docs/wiki' | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host 'git commit failed.'; exit 1 }
        & git push --quiet 2>&1 | ForEach-Object { Write-Host $_.ToString() }
        if ($LASTEXITCODE -ne 0) { Write-Host 'git push failed.'; exit 1 }
        Write-Host 'Wiki published.'
        exit 0
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
