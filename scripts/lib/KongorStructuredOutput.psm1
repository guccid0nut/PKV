# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Unified Dual-Mode Output Emitter for Project Kongor Inspection Scripts (Option H: rz_structured_data.h / rz_table.h).

.DESCRIPTION
    Provides New-KongorReport and Write-KongorReport so inspection and verification scripts
    emit either human-readable ASCII tables or deterministic machine-parseable JSON (-Json)
    from a single data model.
#>

function New-KongorReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,

        [Parameter(Mandatory = $false)]
        [string]$Status = "PASS",

        [Parameter(Mandatory = $false)]
        [hashtable]$Summary = @{},

        [Parameter(Mandatory = $false)]
        [array]$Rows = @()
    )

    return [ordered]@{
        schema_version = "1.0.0"
        title          = $Title
        status         = $Status
        generated_at   = (Get-Date).ToString("o")
        summary        = $Summary
        rows           = @($Rows)
    }
}

function Write-KongorReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Report,

        [Parameter(Mandatory = $false)]
        [switch]$Json
    )

    if ($Json) {
        Write-Output ($Report | ConvertTo-Json -Depth 8 -Compress)
        return
    }

    $statusColor = switch ($Report.status) {
        "PASS"    { [ConsoleColor]::Green }
        "SUCCESS" { [ConsoleColor]::Green }
        "FIXED"   { [ConsoleColor]::Cyan }
        "BROKEN"  { [ConsoleColor]::Yellow }
        default   { [ConsoleColor]::Red }
    }

    Write-Host ("=" * 72) -ForegroundColor Cyan
    Write-Host " [$($Report.status)] $($Report.title)" -ForegroundColor $statusColor
    Write-Host ("=" * 72) -ForegroundColor Cyan

    if ($Report.summary -and $Report.summary.Count -gt 0) {
        foreach ($k in $Report.summary.Keys) {
            Write-Host ("  {0,-28} : {1}" -f $k, $Report.summary[$k]) -ForegroundColor Gray
        }
    }

    if ($Report.rows -and $Report.rows.Count -gt 0) {
        Write-Host ("-" * 72) -ForegroundColor DarkGray
        ($Report.rows | ForEach-Object { [PSCustomObject]$_ } | Format-Table -AutoSize | Out-String).Trim() | Write-Host
    }
}

Export-ModuleMember -Function New-KongorReport, Write-KongorReport
