# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Activates the versioned git hooks in .githooks/ for this clone (idempotent).
.DESCRIPTION
    Sets core.hooksPath=.githooks so every clone (and every AI agent / MCP tool running git in it)
    uses the same, reviewed hooks instead of whatever happens to sit in .git/hooks.
    Any legacy .git/hooks/pre-push is backed up to .git/hooks/pre-push.legacy-<timestamp> (it is
    ignored once core.hooksPath is set, but kept so nothing is lost).

    Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-git-hooks.ps1
            add -Uninstall to go back to .git/hooks
#>
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
function Write-HookLog([string]$Message, [string]$Level = "INFO") {
    $color = switch ($Level) { "SUCCESS" { "Green" } "WARN" { "Yellow" } "ERROR" { "Red" } default { "Cyan" } }
    Write-Host ("[{0}] [{1}] [install-git-hooks] {2}" -f (Get-Date).ToString("HH:mm:ss"), $Level, $Message) -ForegroundColor $color
}

Push-Location $RepoRoot
try {
    $current = (& git config --local --get core.hooksPath) 2>$null
    Write-HookLog "Current core.hooksPath: '$current'"

    if ($Uninstall) {
        & git config --local --unset core.hooksPath
        Write-HookLog "core.hooksPath removed; git falls back to .git/hooks." "SUCCESS"
        exit 0
    }

    $hookFile = Join-Path $RepoRoot ".githooks\pre-push"
    if (-not (Test-Path $hookFile)) { Write-HookLog "Missing $hookFile" "ERROR"; exit 1 }
    if ((Get-Content $hookFile -Raw) -match "`r") {
        Write-HookLog ".githooks/pre-push has CRLF line endings; sh may fail. Re-checkout with: git checkout -- .githooks" "WARN"
    }

    $legacy = Join-Path $RepoRoot ".git\hooks\pre-push"
    if (Test-Path $legacy) {
        $backup = "$legacy.legacy-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
        Copy-Item $legacy $backup
        Write-HookLog "Backed up legacy hook to $backup (ignored from now on)." "INFO"
    }

    & git config --local core.hooksPath .githooks
    $after = (& git config --local --get core.hooksPath)
    if ($after -ne ".githooks") { Write-HookLog "Failed to set core.hooksPath (got '$after')." "ERROR"; exit 1 }
    Write-HookLog "core.hooksPath = .githooks (active hooks: $((Get-ChildItem (Join-Path $RepoRoot '.githooks') -File).Name -join ', '))" "SUCCESS"
    exit 0
} finally {
    Pop-Location
}
