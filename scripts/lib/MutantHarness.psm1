# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Shared hermetic workspace, C/C++/C# comment stripping, and PE64 Export Directory inspection utilities for Project Kongor mutant suites.
#>
Set-StrictMode -Version Latest

function New-KongorMutantWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,

        [Parameter(Mandatory = $false)]
        [string[]]$RelativeFiles = @()
    )

    $guid = [guid]::NewGuid().ToString("N").Substring(0, 12)
    $scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("kongor_{0}_{1}" -f $Prefix, $guid)
    New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null

    foreach ($rel in $RelativeFiles) {
        if ([string]::IsNullOrWhiteSpace($rel)) { continue }
        $srcPath = Join-Path $SourceRoot $rel
        if (Test-Path -LiteralPath $srcPath) {
            $dstPath = Join-Path $scratchRoot $rel
            $dstDir = Split-Path -Parent $dstPath
            if (-not (Test-Path -LiteralPath $dstDir)) {
                New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force
        }
    }

    return $scratchRoot
}

function Remove-KongorMutantWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspacePath,

        [Parameter(Mandatory = $false)]
        [switch]$KeepScratch = $false
    )

    if ($KeepScratch) {
        Write-Host ("[MUTANT HARNESS] Preserving scratch workspace for post-mortem: {0}" -f $WorkspacePath) -ForegroundColor Yellow
        return
    }

    if ([string]::IsNullOrWhiteSpace($WorkspacePath) -or -not (Test-Path -LiteralPath $WorkspacePath)) {
        return
    }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $WorkspacePath -Recurse -Force -ErrorAction Stop
            return
        } catch {
            [System.GC]::Collect()
            [System.GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds (40 * $attempt)
            if ($attempt -eq 3) {
                throw ("Failed to clean up mutant scratch workspace '{0}' after 3 attempts: {1}. Inspect open handles or run with -KeepScratch." -f $WorkspacePath, $_.Exception.Message)
            }
        }
    }
}

function Remove-KongorCodeComments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$SourceText
    )

    if ([string]::IsNullOrEmpty($SourceText)) {
        return ""
    }

    # 1. Strip /* ... */ multi-line block comments first
    $noBlock = [regex]::Replace($SourceText, '/\*[\s\S]*?\*/', '')
    # 2. Strip // ... single-line comments while preserving newlines
    $lines = $noBlock -split "`r?`n"
    $cleanedLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        $cleaned = [regex]::Replace($line, '//.*$', '')
        $cleanedLines.Add($cleaned)
    }
    return ($cleanedLines -join "`n")
}

function Convert-KongorPeRvaToOffset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [uint32]$Rva,

        [Parameter(Mandatory = $true)]
        [array]$Sections
    )

    foreach ($sec in $Sections) {
        $va = [uint32]$sec.VirtualAddress
        $span = [Math]::Max([uint32]$sec.VirtualSize, [uint32]$sec.SizeOfRawData)
        if ($Rva -ge $va -and $Rva -lt ($va + $span)) {
            return [int](($Rva - $va) + [uint32]$sec.PointerToRawData)
        }
    }
    return -1
}

function Get-KongorPe64Exports {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinaryPath
    )

    if (-not (Test-Path -LiteralPath $BinaryPath)) {
        throw ("PE64 binary not found at '{0}'." -f $BinaryPath)
    }

    $bytes = [System.IO.File]::ReadAllBytes($BinaryPath)
    if ($bytes.Length -lt 0x100) {
        return [ordered]@{ ValidPe64 = $false; Reason = "FileTooSmall"; Exports = @(); Offsets = @{} }
    }

    # Check MZ magic (0x5A4D)
    $mz = [BitConverter]::ToUInt16($bytes, 0)
    if ($mz -ne 0x5A4D) {
        return [ordered]@{ ValidPe64 = $false; Reason = "InvalidDosMagic"; Exports = @(); Offsets = @{} }
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or ($peOffset + 0x18 + 0x78) -gt $bytes.Length) {
        return [ordered]@{ ValidPe64 = $false; Reason = "InvalidPeOffset"; Exports = @(); Offsets = @{} }
    }

    # Check PE\0\0 signature (0x00004550)
    $peSig = [BitConverter]::ToUInt32($bytes, $peOffset)
    if ($peSig -ne 0x00004550) {
        return [ordered]@{ ValidPe64 = $false; Reason = "InvalidPeSignature"; Exports = @(); Offsets = @{} }
    }

    $numSections = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
    $sizeOfOptionalHeader = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optOffset = $peOffset + 24
    $optMagic = [BitConverter]::ToUInt16($bytes, $optOffset)

    # PE32+ magic = 0x20B (PE32 = 0x10B)
    $dataDirOffset = if ($optMagic -eq 0x20B) { $optOffset + 112 } elseif ($optMagic -eq 0x10B) { $optOffset + 96 } else { -1 }
    if ($dataDirOffset -lt 0 -or ($dataDirOffset + 8) -gt $bytes.Length) {
        return [ordered]@{ ValidPe64 = $false; Reason = "UnsupportedOptionalHeaderMagic"; Exports = @(); Offsets = @{} }
    }

    $exportDirRva = [BitConverter]::ToUInt32($bytes, $dataDirOffset)
    $exportDirSize = [BitConverter]::ToUInt32($bytes, $dataDirOffset + 4)
    if ($exportDirRva -eq 0 -or $exportDirSize -eq 0) {
        return [ordered]@{ ValidPe64 = $true; Reason = "NoExportDirectory"; Exports = @(); Offsets = @{} }
    }

    $sectionTableOffset = $optOffset + $sizeOfOptionalHeader
    $sections = New-Object System.Collections.Generic.List[object]
    for ($i = 0; $i -lt $numSections; $i++) {
        $secOff = $sectionTableOffset + ($i * 40)
        if (($secOff + 40) -gt $bytes.Length) { break }
        $sections.Add([PSCustomObject]@{
            VirtualSize      = [BitConverter]::ToUInt32($bytes, $secOff + 8)
            VirtualAddress   = [BitConverter]::ToUInt32($bytes, $secOff + 12)
            SizeOfRawData    = [BitConverter]::ToUInt32($bytes, $secOff + 16)
            PointerToRawData = [BitConverter]::ToUInt32($bytes, $secOff + 20)
        })
    }

    $exportFileOffset = Convert-KongorPeRvaToOffset -Rva $exportDirRva -Sections $sections.ToArray()
    if ($exportFileOffset -lt 0 -or ($exportFileOffset + 40) -gt $bytes.Length) {
        return [ordered]@{ ValidPe64 = $false; Reason = "UnmappedExportDirectoryRva"; Exports = @(); Offsets = @{} }
    }

    $numberOfFunctions = [BitConverter]::ToUInt32($bytes, $exportFileOffset + 20)
    $numberOfNames = [BitConverter]::ToUInt32($bytes, $exportFileOffset + 24)
    $addressOfFunctionsRva = [BitConverter]::ToUInt32($bytes, $exportFileOffset + 28)
    $addressOfNamesRva = [BitConverter]::ToUInt32($bytes, $exportFileOffset + 32)
    $addressOfNameOrdinalsRva = [BitConverter]::ToUInt32($bytes, $exportFileOffset + 36)

    $functionsArrayOffset = Convert-KongorPeRvaToOffset -Rva $addressOfFunctionsRva -Sections $sections.ToArray()
    $namesArrayOffset = Convert-KongorPeRvaToOffset -Rva $addressOfNamesRva -Sections $sections.ToArray()
    $ordinalsArrayOffset = Convert-KongorPeRvaToOffset -Rva $addressOfNameOrdinalsRva -Sections $sections.ToArray()
    if ($namesArrayOffset -lt 0 -or $functionsArrayOffset -lt 0 -or $ordinalsArrayOffset -lt 0) {
        return [ordered]@{ ValidPe64 = $false; Reason = "UnmappedAddressOfNamesRva"; Exports = @(); Offsets = @{}; FunctionOffsets = @{}; FunctionRvas = @{} }
    }

    $exportNames = New-Object System.Collections.Generic.List[string]
    $exportOffsets = [ordered]@{}
    $functionOffsets = [ordered]@{}
    $functionRvas = [ordered]@{}

    for ($n = 0; $n -lt $numberOfNames; $n++) {
        $ptrOff = $namesArrayOffset + ($n * 4)
        $ordOff = $ordinalsArrayOffset + ($n * 2)
        if (($ptrOff + 4) -gt $bytes.Length -or ($ordOff + 2) -gt $bytes.Length) { break }
        $nameRva = [BitConverter]::ToUInt32($bytes, $ptrOff)
        $ordinal = [BitConverter]::ToUInt16($bytes, $ordOff)
        $nameFileOffset = Convert-KongorPeRvaToOffset -Rva $nameRva -Sections $sections.ToArray()
        if ($nameFileOffset -lt 0 -or $nameFileOffset -ge $bytes.Length) { continue }

        $endIdx = $nameFileOffset
        while ($endIdx -lt $bytes.Length -and $bytes[$endIdx] -ne 0) {
            $endIdx++
        }
        $nameStr = [System.Text.Encoding]::ASCII.GetString($bytes, $nameFileOffset, ($endIdx - $nameFileOffset))
        if (-not [string]::IsNullOrEmpty($nameStr)) {
            $exportNames.Add($nameStr)
            $exportOffsets[$nameStr] = $nameFileOffset
            if ($ordinal -lt $numberOfFunctions) {
                $fnRvaOff = $functionsArrayOffset + ($ordinal * 4)
                if (($fnRvaOff + 4) -le $bytes.Length) {
                    $fnRva = [BitConverter]::ToUInt32($bytes, $fnRvaOff)
                    $fnFileOff = Convert-KongorPeRvaToOffset -Rva $fnRva -Sections $sections.ToArray()
                    $functionRvas[$nameStr] = $fnRva
                    $functionOffsets[$nameStr] = $fnFileOff
                }
            }
        }
    }

    return [ordered]@{
        ValidPe64       = $true
        Reason          = "OK"
        Exports         = $exportNames.ToArray()
        Offsets         = $exportOffsets
        FunctionOffsets = $functionOffsets
        FunctionRvas    = $functionRvas
    }
}

Export-ModuleMember -Function @(
    'New-KongorMutantWorkspace',
    'Remove-KongorMutantWorkspace',
    'Remove-KongorCodeComments',
    'Get-KongorPe64Exports'
)
