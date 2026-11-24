# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Generates deterministic Multi-OS pkv-v5 CDN manifests (wac, lac, mac), CAS SHA-256 objects,
    and R2 upload plans ready for remote R2/CDN (e.g. cdn.example.com) and local /cdn Master Server serving.

.DESCRIPTION
    Cleanroom Windows PowerShell 5.1 front-end to tools/export/pkv_cdn_manifest.py enforcing
    NEXUS.Contracts.Cdn.PkvManifestValidator & ManifestSignatureVerifier invariants:
      - protocol = "pkv-v5", version >= 5.0.0 (from Directory.Build.props), mode = "overlay",
        objectLayout = "sha256", hashAlgorithm = "SHA-256", monotonic sequence >= 1.
      - Partitioned OS variants: wac (Windows x64), lac (Linux x64), mac (macOS Universal-64).
      - IEEE P1363 (64-byte r || s) ECDSA NIST P-256 SHA-256 canonical transcript signatures.
      - Optional direct R2 upload via Cloudflare Wrangler (-PublishToR2).

.EXAMPLE
    .\scripts\release\generate_pkv_cdn_manifests.ps1 -BaselineEmpty -GenerateEphemeralKey
    .\scripts\release\generate_pkv_cdn_manifests.ps1 -OverrideFrom "games\client\override" -AllowUnsignedLoopback
    .\scripts\release\generate_pkv_cdn_manifests.ps1 -OverrideFrom "games\client\override" -SigningKeyPath "<path-to-pkv_cdn_p256.pem>" -PublishToR2
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [string]$Version = "",

    [Parameter(Mandatory = $false)]
    [int]$Sequence = 0,

    [Parameter(Mandatory = $false)]
    [string[]]$Variants = @("wac", "lac", "mac"),

    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$OverrideFrom = "",

    [Parameter(Mandatory = $false)]
    [string[]]$Files = @(),

    [Parameter(Mandatory = $false)]
    [string[]]$Remove = @(),

    [Parameter(Mandatory = $false)]
    [switch]$BaselineEmpty,

    [Parameter(Mandatory = $false)]
    [string]$SigningKeyPath = "",

    [Parameter(Mandatory = $false)]
    [switch]$GenerateEphemeralKey,

    [Parameter(Mandatory = $false)]
    [switch]$AllowUnsignedLoopback,

    [Parameter(Mandatory = $false)]
    [switch]$PublishToR2,

    [Parameter(Mandatory = $false)]
    [string]$R2Bucket = "kongor-patches"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath "..\..")).Path
$GeneratorPy = Join-Path -Path $RepoRoot -ChildPath "tools\export\pkv_cdn_manifest.py"

function Write-PkvCdnLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $false)][string]$Level = "INFO",
        [Parameter(Mandatory = $false)][System.ConsoleColor]$Color = [System.ConsoleColor]::Cyan
    )
    $ts = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    Write-Host -Object "[PKV-CDN $ts][$Level] $Message" -ForegroundColor $Color
}

if (-not (Test-Path -LiteralPath $GeneratorPy -PathType Leaf)) {
    throw "[PKV-CDN FAILED] Required Python generator helper missing at '$GeneratorPy'. Verify repository checkout integrity."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path -Path $RepoRoot -ChildPath "artifacts\cdn-pkv-v5"
}

$EffectiveKeyPath = $SigningKeyPath
if ([string]::IsNullOrWhiteSpace($EffectiveKeyPath) -and (-not [string]::IsNullOrWhiteSpace($env:KONGOR_CDN_SIGNING_KEY_PATH))) {
    $EffectiveKeyPath = $env:KONGOR_CDN_SIGNING_KEY_PATH
}

if ([string]::IsNullOrWhiteSpace($EffectiveKeyPath) -and (-not $GenerateEphemeralKey.IsPresent) -and (-not $AllowUnsignedLoopback.IsPresent)) {
    throw "[PKV-CDN SIGNING REQUIRED] Provide -SigningKeyPath <ecdsa-p256.pem> (or `$env:KONGOR_CDN_SIGNING_KEY_PATH), -GenerateEphemeralKey for cleanroom CI/test verification, or -AllowUnsignedLoopback for local 127.0.0.1 /cdn serving. Example: .\scripts\release\generate_pkv_cdn_manifests.ps1 -BaselineEmpty -GenerateEphemeralKey"
}

if ($PublishToR2.IsPresent -and $AllowUnsignedLoopback.IsPresent) {
    throw "[PKV-CDN SECURITY BLOCKED] Cannot combine -PublishToR2 with -AllowUnsignedLoopback! Production R2 CDN manifests MUST carry a valid ECDSA P-256 signature."
}

$pyArgs = New-Object -TypeName 'System.Collections.Generic.List[string]'
$pyArgs.Add("-m")
$pyArgs.Add("tools.export.pkv_cdn_manifest")
$pyArgs.Add("--repo-root")
$pyArgs.Add($RepoRoot)
$pyArgs.Add("--output-dir")
$pyArgs.Add($OutputDir)
$pyArgs.Add("--variants")
$pyArgs.Add(($Variants -join ","))

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $pyArgs.Add("--version")
    $pyArgs.Add($Version)
}
if ($Sequence -gt 0) {
    $pyArgs.Add("--sequence")
    $pyArgs.Add([string]$Sequence)
}
if (-not [string]::IsNullOrWhiteSpace($BundleRoot)) {
    $pyArgs.Add("--bundle-root")
    $pyArgs.Add($BundleRoot)
}
if (-not [string]::IsNullOrWhiteSpace($OverrideFrom)) {
    $pyArgs.Add("--override-from")
    $pyArgs.Add($OverrideFrom)
}
if ($Files.Count -gt 0) {
    $pyArgs.Add("--files")
    foreach ($f in $Files) { $pyArgs.Add($f) }
}
if ($Remove.Count -gt 0) {
    $pyArgs.Add("--remove")
    foreach ($r in $Remove) { $pyArgs.Add($r) }
}
if ($BaselineEmpty.IsPresent) {
    $pyArgs.Add("--baseline-empty")
}
if (-not [string]::IsNullOrWhiteSpace($EffectiveKeyPath)) {
    $pyArgs.Add("--signing-key-path")
    $pyArgs.Add($EffectiveKeyPath)
}
if ($GenerateEphemeralKey.IsPresent) {
    $pyArgs.Add("--generate-ephemeral-key")
}
if ($AllowUnsignedLoopback.IsPresent) {
    $pyArgs.Add("--allow-unsigned-loopback")
}

Write-PkvCdnLog -Message "Generating pkv-v5 Multi-OS CDN manifests (variants=$($Variants -join ', '), output='$OutputDir')..." -Color Cyan
Push-Location -LiteralPath $RepoRoot
try {
    & python @($pyArgs.ToArray())
    $pyExit = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($pyExit -ne 0) {
    throw "[PKV-CDN FAILED] pkv_cdn_manifest.py failed with exit code $pyExit. Diagnose via: python -m tools.export.pkv_cdn_manifest --help"
}

$ResolvedOut = (Resolve-Path -LiteralPath $OutputDir).Path
foreach ($v in $Variants) {
    $mPath = Join-Path -Path $ResolvedOut -ChildPath "$v\manifest.json"
    if (-not (Test-Path -LiteralPath $mPath -PathType Leaf)) {
        throw "[PKV-CDN FAILED] Expected manifest '$mPath' was not created."
    }
}

if ($PublishToR2.IsPresent) {
    $wranglerCmd = Get-Command -Name "wrangler" -ErrorAction SilentlyContinue
    if ($null -eq $wranglerCmd) {
        throw "[PKV-CDN R2 FAILED] Cloudflare 'wrangler' CLI is not installed on PATH. Install via 'npm install -g wrangler' before passing -PublishToR2."
    }
    $planPath = Join-Path -Path $ResolvedOut -ChildPath "r2_upload_plan.json"
    $planObj = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json

    # Upload CAS objects & immutable release snapshots BEFORE live {wac,lac,mac}/manifest.json pointers
    $stagedUploads = @($planObj.objects | Where-Object { $_.r2Key -like "objects/*" -or $_.r2Key -like "releases/*" })
    $liveUploads = @($planObj.objects | Where-Object { $_.r2Key -notlike "objects/*" -and $_.r2Key -notlike "releases/*" })

    foreach ($item in ($stagedUploads + $liveUploads)) {
        $localFile = Join-Path -Path $ResolvedOut -ChildPath ($item.localPath -replace '/', '\')
        $r2Target = "$R2Bucket/$($item.r2Key)"
        Write-PkvCdnLog -Message "Uploading R2 object '$r2Target' (Cache-Control: $($item.cacheControl))..." -Color Gray
        & wrangler r2 object put $r2Target --file $localFile --content-type $item.contentType --cache-control $item.cacheControl --remote
        if ($LASTEXITCODE -ne 0) {
            throw "[PKV-CDN R2 FAILED] wrangler r2 object put failed for '$r2Target' (exit $LASTEXITCODE)."
        }
    }
    Write-PkvCdnLog -Message "Published $($planObj.objects.Count) pkv-v5 objects & manifests to R2 bucket '$R2Bucket'." -Color Green
}

Write-PkvCdnLog -Message "pkv-v5 CDN layout ready at '$ResolvedOut' (wac/lac/mac manifests + pkv-info.json + objects/ CAS)." -Color Green
