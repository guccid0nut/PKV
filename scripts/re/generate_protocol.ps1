# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

# ============================================================================
# PROJECT KONGOR: CROSS-LANGUAGE PROTOCOL CODE GENERATION SCRIPT
# Invokes native/bin/kongor-metagen.exe to compile data/schemas/protocol.json
# into synchronized C++, C#, and Lua bindings.
# ============================================================================

[CmdletBinding()]
param(
    [string]$SchemaPath = "data/schemas/protocol.json",
    [string]$BaselineSchemaPath = "",
    [string]$CppOutPath = "native/include/generated/kongor_protocol.h",
    [string]$CsOutPath  = "src/libs/contracts/Generated/KongorProtocol.g.cs",
    [string]$LuaOutPath = "games/client/override/ui/scripts/generated/protocol.lua",
    [switch]$Check = $false,
    [switch]$VerifyWireCompatibilityOnly = $false,
    [string]$JsonOutput = "",
    [switch]$VerboseOutput = $false
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent ($PSScriptRoot | Split-Path -Parent)

# satisfies: #246 (repowise-semantic-protocol-breaking-change-guard)
function Test-ProtocolSemanticWireCompatibility {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$BaselineJsonText,
        [Parameter(Mandatory = $true)][string]$CandidateJsonText
    )

    $baseSchema = $BaselineJsonText | ConvertFrom-Json
    $candSchema = $CandidateJsonText | ConvertFrom-Json
    $violations = New-Object System.Collections.Generic.List[object]

    $baseMajor = if ($baseSchema.version -and ([string]$baseSchema.version -match '^(\d+)\.')) { [int]$Matches[1] } else { 1 }
    $candMajor = if ($candSchema.version -and ([string]$candSchema.version -match '^(\d+)\.')) { [int]$Matches[1] } else { 1 }
    $isMajorBump = ($candMajor -gt $baseMajor)

    # 1. Check constants / packet endpoints (REMOVED_ENDPOINT & REUSED_FIELD_TAG)
    $candConstByName = @{}
    $candPacketByOpcode = @{}
    foreach ($cc in @($candSchema.constants)) {
        if ($null -eq $cc -or [string]::IsNullOrWhiteSpace([string]$cc.name)) { continue }
        $cName = [string]$cc.name
        $candConstByName[$cName] = $cc
        if ($cName -like "KONGOR_PACKET_*") {
            $opVal = ([string]$cc.value).Trim().ToLowerInvariant()
            $candPacketByOpcode[$opVal] = $cName
        }
    }

    foreach ($bc in @($baseSchema.constants)) {
        if ($null -eq $bc -or [string]::IsNullOrWhiteSpace([string]$bc.name)) { continue }
        $bName = [string]$bc.name
        $bVal = ([string]$bc.value).Trim().ToLowerInvariant()
        if (-not $candConstByName.ContainsKey($bName)) {
            $violations.Add([PSCustomObject]@{
                kind    = "REMOVED_ENDPOINT"
                target  = $bName
                message = "Protocol endpoint/constant '$bName' (value=$($bc.value)) was removed from protocol.json (#246)."
            })
            if ($bName -like "KONGOR_PACKET_*" -and $candPacketByOpcode.ContainsKey($bVal) -and $candPacketByOpcode[$bVal] -ne $bName) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REUSED_FIELD_TAG"
                    target  = "$bName -> $($candPacketByOpcode[$bVal]) ($($bc.value))"
                    message = "Packet opcode tag '$($bc.value)' previously assigned to '$bName' was reused by '$($candPacketByOpcode[$bVal])' (#246)."
                })
            }
        } else {
            $ccMatch = $candConstByName[$bName]
            $cVal = ([string]$ccMatch.value).Trim().ToLowerInvariant()
            if ($bName -like "KONGOR_PACKET_*" -and $bVal -ne $cVal) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REUSED_FIELD_TAG"
                    target  = $bName
                    message = "Packet endpoint '$bName' changed wire opcode tag from '$($bc.value)' to '$($ccMatch.value)' (#246)."
                })
            }
        }
    }

    # Optional explicit endpoints array support
    $candEndpointsByName = @{}
    foreach ($ce in @($candSchema.endpoints)) {
        if ($null -ne $ce -and -not [string]::IsNullOrWhiteSpace([string]$ce.name)) {
            $candEndpointsByName[[string]$ce.name] = $ce
        }
    }
    foreach ($be in @($baseSchema.endpoints)) {
        if ($null -ne $be -and -not [string]::IsNullOrWhiteSpace([string]$be.name)) {
            $eName = [string]$be.name
            if (-not $candEndpointsByName.ContainsKey($eName)) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REMOVED_ENDPOINT"
                    target  = $eName
                    message = "RPC endpoint '$eName' was removed from protocol.json (#246)."
                })
            }
        }
    }

    # 1b. Check enums (REMOVED_ENDPOINT, REUSED_FIELD_TAG, DUPLICATE_ENUM_VALUE)
    $candEnumByName = @{}
    foreach ($ce in @($candSchema.enums)) {
        if ($null -eq $ce -or [string]::IsNullOrWhiteSpace([string]$ce.name)) { continue }
        $eName = [string]$ce.name
        $candEnumByName[$eName] = $ce
        $seenVals = @{}
        foreach ($ev in @($ce.values)) {
            if ($null -eq $ev -or [string]::IsNullOrWhiteSpace([string]$ev.name)) { continue }
            $vKey = ([string]$ev.value).Trim().ToLowerInvariant()
            if ($seenVals.ContainsKey($vKey)) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REUSED_FIELD_TAG"
                    target  = "${eName}.$($ev.name)"
                    message = "Enum '$eName' assigns duplicate opcode value '$($ev.value)' to both '$($seenVals[$vKey])' and '$($ev.name)' (#246)."
                })
            } else {
                $seenVals[$vKey] = [string]$ev.name
            }
        }
    }

    foreach ($be in @($baseSchema.enums)) {
        if ($null -eq $be -or [string]::IsNullOrWhiteSpace([string]$be.name)) { continue }
        $eName = [string]$be.name
        if (-not $candEnumByName.ContainsKey($eName)) {
            $violations.Add([PSCustomObject]@{
                kind    = "REMOVED_ENDPOINT"
                target  = $eName
                message = "Protocol enum '$eName' was removed from protocol.json (#246)."
            })
            continue
        }
        $ceMatch = $candEnumByName[$eName]
        $candValByName = @{}
        foreach ($cv in @($ceMatch.values)) {
            if ($null -ne $cv -and -not [string]::IsNullOrWhiteSpace([string]$cv.name)) {
                $candValByName[[string]$cv.name] = ([string]$cv.value).Trim().ToLowerInvariant()
            }
        }
        foreach ($bv in @($be.values)) {
            if ($null -eq $bv -or [string]::IsNullOrWhiteSpace([string]$bv.name)) { continue }
            $mName = [string]$bv.name
            $bVal = ([string]$bv.value).Trim().ToLowerInvariant()
            if (-not $candValByName.ContainsKey($mName)) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REMOVED_ENDPOINT"
                    target  = "${eName}.${mName}"
                    message = "Protocol enum member '${eName}.${mName}' ($($bv.value)) was removed from protocol.json (#246)."
                })
            } elseif ($candValByName[$mName] -ne $bVal) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REUSED_FIELD_TAG"
                    target  = "${eName}.${mName}"
                    message = "Protocol enum member '${eName}.${mName}' changed wire tag from '$($bv.value)' to '$($candValByName[$mName])' (#246)."
                })
            }
        }
    }

    # 2. Check wire structs and fields (REMOVED_ENDPOINT, REMOVED_FIELD, RETYPED_FIELD, REUSED_FIELD_TAG, NEW_REQUIRED_FIELD)
    $candStructByName = @{}
    foreach ($cs in @($candSchema.structs)) {
        if ($null -ne $cs -and -not [string]::IsNullOrWhiteSpace([string]$cs.name)) {
            $candStructByName[[string]$cs.name] = $cs
        }
    }

    foreach ($bs in @($baseSchema.structs)) {
        if ($null -eq $bs -or [string]::IsNullOrWhiteSpace([string]$bs.name)) { continue }
        $sName = [string]$bs.name
        if (-not $candStructByName.ContainsKey($sName)) {
            $violations.Add([PSCustomObject]@{
                kind    = "REMOVED_ENDPOINT"
                target  = $sName
                message = "Wire struct '$sName' was removed from protocol.json (#246)."
            })
            continue
        }

        $cs = $candStructByName[$sName]
        $baseFields = @($bs.fields)
        $candFields = @($cs.fields)

        $baseFieldByName = @{}
        $baseFieldTagOrIndex = @{}
        for ($i = 0; $i -lt $baseFields.Count; $i++) {
            $bf = $baseFields[$i]
            $fName = [string]$bf.name
            $baseFieldByName[$fName] = $bf
            $tagVal = if ($null -ne $bf.tag) { [int]$bf.tag } else { $i }
            $baseFieldTagOrIndex[$tagVal] = $fName
        }

        $candFieldByName = @{}
        $candTagByName = @{}
        for ($i = 0; $i -lt $candFields.Count; $i++) {
            $cf = $candFields[$i]
            $fName = [string]$cf.name
            $candFieldByName[$fName] = $cf
            $candTagByName[$fName] = if ($null -ne $cf.tag) { [int]$cf.tag } else { $i }
        }

        for ($i = 0; $i -lt $candFields.Count; $i++) {
            $cf = $candFields[$i]
            $fName = [string]$cf.name
            $tagVal = [int]$candTagByName[$fName]

            # Check REUSED_FIELD_TAG when a slot/tag is occupied by a different field name
            if ($baseFieldTagOrIndex.ContainsKey($tagVal)) {
                $origFieldName = $baseFieldTagOrIndex[$tagVal]
                if ($origFieldName -ne $fName) {
                    $violations.Add([PSCustomObject]@{
                        kind    = "REUSED_FIELD_TAG"
                        target  = "${sName}.${fName} (slot/tag $tagVal)"
                        message = "Struct '$sName' slot/tag $tagVal previously used by '$origFieldName' was reused by '$fName' (#246)."
                    })
                }
            }

            # Check NEW_REQUIRED_FIELD on existing wire struct without major version bump
            if (-not $baseFieldByName.ContainsKey($fName)) {
                $isOptional = ($null -ne $cf.optional -and [bool]$cf.optional)
                if ((-not $isOptional) -and (-not $isMajorBump)) {
                    $violations.Add([PSCustomObject]@{
                        kind    = "NEW_REQUIRED_FIELD"
                        target  = "${sName}.${fName}"
                        message = "New required field '${sName}.${fName}' added to fixed-layout wire struct without optional=true or major schema version bump (#246)."
                    })
                }
            }
        }

        foreach ($bf in $baseFields) {
            $fName = [string]$bf.name
            if (-not $candFieldByName.ContainsKey($fName)) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REMOVED_FIELD"
                    target  = "${sName}.${fName}"
                    message = "Wire field '${sName}.${fName}' was removed from struct '$sName' (#246)."
                })
                continue
            }

            $cf = $candFieldByName[$fName]
            $bItemsJson = if ($null -ne $bf.items) { ($bf.items | ConvertTo-Json -Compress -Depth 6) } else { "" }
            $cItemsJson = if ($null -ne $cf.items) { ($cf.items | ConvertTo-Json -Compress -Depth 6) } else { "" }
            $bType = "$($bf.type)|$($bf.element_type)|$($bf.count)|$($bf.key_type)|$($bf.value_type)|$bItemsJson"
            $cType = "$($cf.type)|$($cf.element_type)|$($cf.count)|$($cf.key_type)|$($cf.value_type)|$cItemsJson"
            if ($bType -ne $cType) {
                $violations.Add([PSCustomObject]@{
                    kind    = "RETYPED_FIELD"
                    target  = "${sName}.${fName}"
                    message = "Wire field '${sName}.${fName}' changed type signature from '$($bf.type)' to '$($cf.type)' (#246)."
                })
            }

            if ($null -ne $bf.tag -and $null -ne $cf.tag -and ([int]$bf.tag -ne [int]$cf.tag)) {
                $violations.Add([PSCustomObject]@{
                    kind    = "REUSED_FIELD_TAG"
                    target  = "${sName}.${fName} (slot/tag $($bf.tag)->$($cf.tag))"
                    message = "Wire field '${sName}.${fName}' changed wire tag from $($bf.tag) to $($cf.tag) (#246)."
                })
            }
        }
    }

    return [PSCustomObject]@{
        compatible = ($violations.Count -eq 0)
        count      = $violations.Count
        violations = $violations.ToArray()
    }
}

$resolvedSchemaFile = if ([System.IO.Path]::IsPathRooted($SchemaPath)) { $SchemaPath } else { Join-Path $repoRoot $SchemaPath }
$baselineTextToUse = ""
if (-not [string]::IsNullOrWhiteSpace($BaselineSchemaPath)) {
    $resolvedBase = if ([System.IO.Path]::IsPathRooted($BaselineSchemaPath)) { $BaselineSchemaPath } else { Join-Path $repoRoot $BaselineSchemaPath }
    if (Test-Path -LiteralPath $resolvedBase -PathType Leaf) {
        $baselineTextToUse = [System.IO.File]::ReadAllText($resolvedBase)
    }
} elseif (Test-Path -LiteralPath (Join-Path $repoRoot ".git")) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $gitShowLines = @(& git -C $repoRoot show "HEAD:data/schemas/protocol.json" 2>$null)
        if ($LASTEXITCODE -eq 0 -and $gitShowLines.Count -gt 0) {
            $baselineTextToUse = ($gitShowLines -join "`n")
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }
}

if (-not [string]::IsNullOrWhiteSpace($baselineTextToUse) -and (Test-Path -LiteralPath $resolvedSchemaFile -PathType Leaf)) {
    $candText = [System.IO.File]::ReadAllText($resolvedSchemaFile)
    $wireCompat = Test-ProtocolSemanticWireCompatibility -BaselineJsonText $baselineTextToUse -CandidateJsonText $candText
    if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
        [System.IO.File]::WriteAllText($JsonOutput, ($wireCompat | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    }
    if (-not $wireCompat.compatible) {
        Write-Host "[PROTOCOL-WIRE-GUARD-246] FAILED: $($wireCompat.count) semantic wire-compatibility violation(s) detected:" -ForegroundColor Red
        foreach ($v in $wireCompat.violations) {
            Write-Host "  [$($v.kind)] $($v.target): $($v.message)" -ForegroundColor Red
        }
        exit 2
    } elseif ($VerifyWireCompatibilityOnly) {
        Write-Host "[PROTOCOL-WIRE-GUARD-246] PASS: 0 semantic wire-compatibility violations." -ForegroundColor Green
        exit 0
    }
} elseif ($VerifyWireCompatibilityOnly) {
    Write-Host "[PROTOCOL-WIRE-GUARD-246] PASS: Schema verified." -ForegroundColor Green
    exit 0
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR PROTOCOL METAGEN] Synchronized Protocol Code Generation" -ForegroundColor Cyan
Write-Host " Repo Root: $repoRoot" -ForegroundColor Gray
Write-Host " Schema:    $SchemaPath" -ForegroundColor Gray
Write-Host " C++ Target: $CppOutPath" -ForegroundColor Gray
Write-Host " C# Target:  $CsOutPath" -ForegroundColor Gray
Write-Host " Lua Target: $LuaOutPath" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan

# 1. Locate or compile kongor-metagen executable
$metagenSrc = Join-Path $repoRoot "native/tools/kongor-metagen/main.cpp"
$metagenBin = Join-Path $repoRoot "native/bin/kongor-metagen.exe"
if (-not (Test-Path $metagenBin)) {
    $metagenBin = Join-Path $repoRoot "native/build/kongor_metagen_build/Release/kongor-metagen.exe"
}

$needsMetagenRebuild = (-not (Test-Path $metagenBin))
if ((-not $needsMetagenRebuild) -and (Test-Path $metagenSrc)) {
    $srcMtime = (Get-Item -LiteralPath $metagenSrc).LastWriteTimeUtc
    $binMtime = (Get-Item -LiteralPath $metagenBin).LastWriteTimeUtc
    if ($srcMtime -gt $binMtime) {
        $needsMetagenRebuild = $true
    }
}

if ($needsMetagenRebuild) {
    Write-Host "[KONGOR METAGEN] Building native kongor-metagen CLI..." -ForegroundColor Yellow
    $buildSw = [System.Diagnostics.Stopwatch]::StartNew()
    & cmake --build (Join-Path $repoRoot "native/build") --target kongor-metagen --config Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "[KONGOR METAGEN] Failed to build kongor-metagen.exe! Aborting."
        exit 1
    }
    Write-Host "[KONGOR METAGEN] Build succeeded in $($buildSw.ElapsedMilliseconds)ms." -ForegroundColor Green
    $metagenBin = Join-Path $repoRoot "native/bin/kongor-metagen.exe"
}

# 2. Prepare argument list
$metagenArgs = @(
    "--schema", (Join-Path $repoRoot $SchemaPath),
    "--cpp-out", (Join-Path $repoRoot $CppOutPath),
    "--cs-out", (Join-Path $repoRoot $CsOutPath),
    "--lua-out", (Join-Path $repoRoot $LuaOutPath)
)

if ($Check) {
    $metagenArgs += "--check"
}
if ($VerboseOutput) {
    $metagenArgs += "--verbose"
}

# 3. Invoke native metagen binary
Write-Host "[KONGOR METAGEN] Executing $metagenBin ..." -ForegroundColor Gray
& $metagenBin @metagenArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "[KONGOR METAGEN] Code generation failed with exit code $LASTEXITCODE!" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 4. Sync/Verify ClientPackageFiles mirror & Lua bytecode syntax
$resolvedLua = if ([System.IO.Path]::IsPathRooted($LuaOutPath)) { $LuaOutPath } else { Join-Path $repoRoot $LuaOutPath }
$cpfLuaMirror = Join-Path $repoRoot "games/client/ClientPackageFiles/ui/scripts/generated/protocol.lua"
$isDefaultLuaOut = ($LuaOutPath.Replace('\', '/').Trim().ToLowerInvariant() -eq "games/client/override/ui/scripts/generated/protocol.lua")

if ($Check) {
    if ($isDefaultLuaOut -and (Test-Path -LiteralPath $cpfLuaMirror) -and (Test-Path -LiteralPath $resolvedLua)) {
        $ovrBytes = [System.IO.File]::ReadAllBytes($resolvedLua)
        $cpfBytes = [System.IO.File]::ReadAllBytes($cpfLuaMirror)
        $sameLen = ($ovrBytes.Length -eq $cpfBytes.Length)
        $sameBytes = $sameLen
        if ($sameLen) {
            for ($bi = 0; $bi -lt $ovrBytes.Length; $bi++) {
                if ($ovrBytes[$bi] -ne $cpfBytes[$bi]) { $sameBytes = $false; break }
            }
        }
        if (-not $sameBytes) {
            Write-Host "[PARITY-DRIFT] ClientPackageFiles Lua mirror drift detected: $cpfLuaMirror" -ForegroundColor Red
            exit 1
        }
    }
} else {
    if ($isDefaultLuaOut -and (Test-Path -LiteralPath $resolvedLua) -and (Test-Path -LiteralPath (Split-Path -Parent $cpfLuaMirror))) {
        Copy-Item -LiteralPath $resolvedLua -Destination $cpfLuaMirror -Force
        Write-Host "[KONGOR METAGEN] Synchronized ClientPackageFiles Lua mirror -> $cpfLuaMirror" -ForegroundColor Gray
    }
    if (Test-Path $resolvedLua) {
        Write-Host "[KONGOR METAGEN] Verifying generated Lua bytecode syntax..." -ForegroundColor Gray
        & powershell.exe -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts/verify_client_lua.ps1") -Path $resolvedLua
        if ($LASTEXITCODE -ne 0) {
            Write-Error "[KONGOR METAGEN] Generated Lua file failed bytecode validation!"
            exit 1
        }
    }
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " [KONGOR METAGEN] Protocol synchronization complete in $($sw.ElapsedMilliseconds)ms!" -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Cyan
exit 0
