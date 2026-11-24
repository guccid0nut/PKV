# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    ProjectKongor Ecosystem Unified Version Synchronization Pipeline.
.DESCRIPTION
    Synchronizes the platform version (SemVer 2.0), release date (CalVer), engine protocol version,
    and backwards-compatibility thresholds across all solution projects, Directory.Build.props,
    NexusVersionInfo.cs, and client mod manifests.
.PARAMETER Version
    The SemVer 2.0 platform version (e.g. 5.0.0). Default is 5.0.0.
.PARAMETER BuildDate
    The CalVer build release date (e.g. 2026.09.04). Default is current UTC date in yyyy.MM.dd.
.PARAMETER EngineProtocolVersion
    The modernized K2 game engine protocol version (e.g. 5.0.0). Default is 5.0.0.
.PARAMETER MinCompatibleEngineProtocol
    The minimum backwards-compatible engine protocol version for mods and legacy clients (e.g. 4.10.1). Default is 4.10.1.
.EXAMPLE
    .\scripts\sync-version.ps1 -Version "5.0.0" -BuildDate "2026.09.04"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version = "5.0.0",

    [Parameter(Position = 1)]
    [string]$BuildDate = (Get-Date -Format "yyyy.MM.dd"),

    [Parameter(Position = 2)]
    [string]$EngineProtocolVersion = "5.0.0",

    [Parameter(Position = 3)]
    [string]$MinCompatibleEngineProtocol = "4.10.1",

    [Parameter(Position = 4)]
    [string]$VersionSuffix = "",

    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

$isHelp = $Help -or ($Version -in @("-Help", "--help", "-h", "-?", "help")) -or ($AdditionalArgs -contains "-Help") -or ($AdditionalArgs -contains "--help") -or ($AdditionalArgs -contains "-h") -or ($AdditionalArgs -contains "-?")

if ($isHelp) {
    Get-Help $MyInvocation.MyCommand.Path
    exit 0
}

$ErrorActionPreference = "Stop"

function Write-StepLog {
    param([string]$Message, [string]$Category = "SYNC")
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$timestamp] [$Category] $Message" -ForegroundColor Cyan
}

function Write-SuccessLog {
    param([string]$Message)
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$timestamp] [SUCCESS] $Message" -ForegroundColor Green
}

function Write-InfoLog {
    param([string]$Message)
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff")
    Write-Host "[$timestamp] [INFO] $Message" -ForegroundColor Gray
}

function Write-Utf8PreservingBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $hasBom = $false
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existingBytes = [System.IO.File]::ReadAllBytes($Path)
        $hasBom = ($existingBytes.Length -ge 3 -and $existingBytes[0] -eq 0xEF -and $existingBytes[1] -eq 0xBB -and $existingBytes[2] -eq 0xBF)
    }
    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

# Resolve Repository Root
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

Write-StepLog "Starting ProjectKongor Ecosystem Unified Version Synchronization..."
Write-InfoLog "Target Platform SemVer:             $Version"
Write-InfoLog "Target Build Date (CalVer):         $BuildDate"
Write-InfoLog "Target Engine Protocol Version:     $EngineProtocolVersion"
Write-InfoLog "Target Min Compatible Engine Proto: $MinCompatibleEngineProtocol"
Write-InfoLog "Repository Root Path:               $repoRoot"

# Compute Derived Formats
$calVerShort = $BuildDate.Replace(".", "").Replace("-", "")
$year = $BuildDate.Split(".")[0]
$dashDate = $BuildDate.Replace(".", "-")
$informationalVersion = "v$Version+$BuildDate (Engine Protocol v$EngineProtocolVersion)"
$fullDisplayVersion = "NEXUS v$Version (Build $BuildDate - Engine Protocol v$EngineProtocolVersion)"

# =========================================================================
# 1. UPDATE Directory.Build.props
# =========================================================================
$buildPropsPath = Join-Path $repoRoot "Directory.Build.props"
if (Test-Path -LiteralPath $buildPropsPath) {
    Write-StepLog "Synchronizing Directory.Build.props..."
    $propsContent = Get-Content -LiteralPath $buildPropsPath -Raw -Encoding UTF8

    $versionTag = if ([string]::IsNullOrWhiteSpace($VersionSuffix)) { $Version } else { "$Version-$VersionSuffix" }
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<VersionPrefix>[^<]+</VersionPrefix>',
        "<VersionPrefix>$Version</VersionPrefix>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<VersionSuffix>[^<]*</VersionSuffix>',
        "<VersionSuffix>$VersionSuffix</VersionSuffix>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<Version>[^<]+</Version>',
        "<Version>$versionTag</Version>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<AssemblyVersion>[^<]+</AssemblyVersion>',
        "<AssemblyVersion>$Version.$year</AssemblyVersion>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<FileVersion>[^<]+</FileVersion>',
        "<FileVersion>$Version.$year</FileVersion>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<InformationalVersion>[^<]+</InformationalVersion>',
        "<InformationalVersion>$informationalVersion</InformationalVersion>"
    )
    $propsContent = [System.Text.RegularExpressions.Regex]::Replace(
        $propsContent,
        '<!-- Assembly Metadata & Versioning \([^)]+\) -->',
        "<!-- Assembly Metadata & Versioning (Hybrid SemVer $Version + CalVer $calVerShort + Engine Protocol v$EngineProtocolVersion) -->"
    )

    Write-Utf8PreservingBom -Path $buildPropsPath -Content $propsContent
    Write-SuccessLog "Updated Directory.Build.props (Version: $versionTag, AssemblyVersion: $Version.$year)"
}

# =========================================================================
# 2. UPDATE NexusVersionInfo.cs
# =========================================================================
$nexusVersionPath = Join-Path $repoRoot "src\libs\contracts\Domain\NexusVersionInfo.cs"
if (Test-Path -LiteralPath $nexusVersionPath) {
    Write-StepLog "Synchronizing NexusVersionInfo.cs in NEXUS.Contracts..."
    $nexusContent = Get-Content -LiteralPath $nexusVersionPath -Raw -Encoding UTF8

    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string PlatformVersion = "[^"]+";',
        "public const string PlatformVersion = `"$Version`";"
    )
    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string BuildDate = "[^"]+";',
        "public const string BuildDate = `"$BuildDate`";"
    )
    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string EngineProtocolVersion = "[^"]+";',
        "public const string EngineProtocolVersion = `"$EngineProtocolVersion`";"
    )
    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string MinimumCompatibleEngineProtocolVersion = "[^"]+";',
        "public const string MinimumCompatibleEngineProtocolVersion = `"$MinCompatibleEngineProtocol`";"
    )
    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string FullDisplayVersion = "[^"]+";',
        "public const string FullDisplayVersion = `"$fullDisplayVersion`";"
    )
    $nexusContent = [System.Text.RegularExpressions.Regex]::Replace(
        $nexusContent,
        'public const string InformationalHeaderVersion = "[^"]+";',
        "public const string InformationalHeaderVersion = `"$informationalVersion`";"
    )

    Write-Utf8PreservingBom -Path $nexusVersionPath -Content $nexusContent
    Write-SuccessLog "Updated NexusVersionInfo.cs (FullDisplayVersion: $fullDisplayVersion)"
}

# =========================================================================
# 3. UPDATE Client Mod Manifests (games/client/mods/**/mod.xml)
# =========================================================================
$modsDir = Join-Path $repoRoot "games\client\mods"
if (Test-Path -LiteralPath $modsDir) {
    Write-StepLog "Scanning and updating client mod manifests in $modsDir..."
    $modXmlFiles = Get-ChildItem -Path $modsDir -Filter "mod.xml" -Recurse -File

    foreach ($xmlFile in $modXmlFiles) {
        Write-InfoLog "Processing mod manifest: $($xmlFile.FullName)"
        $xmlContent = Get-Content -LiteralPath $xmlFile.FullName -Raw -Encoding UTF8

        # Restore standard XML declaration
        $xmlContent = [System.Text.RegularExpressions.Regex]::Replace(
            $xmlContent,
            '<\?xml version="[^"]+"',
            '<?xml version="1.0"'
        )

        # Update mod version="..." (excluding the <?xml declaration and mmversion)
        $xmlContent = [System.Text.RegularExpressions.Regex]::Replace(
            $xmlContent,
            '(?<!<\?xml\s+)\bversion="[^"]+"',
            "version=`"$Version`""
        )
        # Update date="..."
        if ($xmlContent -match 'date="[^"]+"') {
            $xmlContent = [System.Text.RegularExpressions.Regex]::Replace(
                $xmlContent,
                'date="[^"]+"',
                "date=`"$dashDate`""
            )
        }
        # Update appversion to works with >= min compatible version
        if ($xmlContent -match 'appversion="[^"]+"') {
            $xmlContent = [System.Text.RegularExpressions.Regex]::Replace(
                $xmlContent,
                'appversion="[^"]+"',
                "appversion=`">=$MinCompatibleEngineProtocol`""
            )
        }

        Write-Utf8PreservingBom -Path $xmlFile.FullName -Content $xmlContent
        Write-SuccessLog "Updated $($xmlFile.Directory.Name)/mod.xml -> Version $Version (appversion >=$MinCompatibleEngineProtocol, date $dashDate)"
    }
}

# =========================================================================
# 4. UPDATE Blender 3D Addon & MCP Tools
# =========================================================================
$blenderManifest = Join-Path $repoRoot "src\tools\blender\io_scene_hon\blender_manifest.toml"
if (Test-Path -LiteralPath $blenderManifest) {
    Write-StepLog "Synchronizing Blender addon manifest..."
    $bmContent = Get-Content -LiteralPath $blenderManifest -Raw -Encoding UTF8
    $bmContent = [System.Text.RegularExpressions.Regex]::Replace($bmContent, '(?m)^version\s*=\s*"[^"]+"', "version = `"$Version`"")
    [System.IO.File]::WriteAllText($blenderManifest, $bmContent, (New-Object System.Text.UTF8Encoding($false)))
    Write-SuccessLog "Updated blender_manifest.toml -> Version $Version"
}

$blenderInit = Join-Path $repoRoot "src\tools\blender\io_scene_hon\__init__.py"
if (Test-Path -LiteralPath $blenderInit) {
    Write-StepLog "Synchronizing Blender addon __init__.py..."
    $biContent = Get-Content -LiteralPath $blenderInit -Raw -Encoding UTF8
    $vParts = $Version.Split(".")
    $major = if ($vParts.Length -gt 0) { [int]$vParts[0] } else { 5 }
    $minor = if ($vParts.Length -gt 1) { [int]$vParts[1] } else { 0 }
    $patch = if ($vParts.Length -gt 2) { [int]$vParts[2] } else { 0 }
    $biContent = [System.Text.RegularExpressions.Regex]::Replace($biContent, '"version":\s*\([0-9,\s]+\)', "`"version`": ($major, $minor, $patch)")
    Write-Utf8PreservingBom -Path $blenderInit -Content $biContent
    Write-SuccessLog "Updated io_scene_hon/__init__.py -> Version ($major, $minor, $patch)"
}

$blenderMcp = Join-Path $repoRoot "src\tools\blender\mcp_server.py"
if (Test-Path -LiteralPath $blenderMcp) {
    $bmcpContent = Get-Content -LiteralPath $blenderMcp -Raw -Encoding UTF8
    $bmcpContent = [System.Text.RegularExpressions.Regex]::Replace($bmcpContent, 'version="[^"]+"', "version=`"$Version`"")
    Write-Utf8PreservingBom -Path $blenderMcp -Content $bmcpContent
    Write-SuccessLog "Updated blender/mcp_server.py -> Version $Version"
}


# =========================================================================
# 5. UPDATE COMPEL & CLI Metrics/Mock Versions
# =========================================================================
$compelMetrics = Join-Path $repoRoot "src\apps\compel\Services\Telemetry\CompelMetrics.cs"
if (Test-Path -LiteralPath $compelMetrics) {
    $cmContent = Get-Content -LiteralPath $compelMetrics -Raw -Encoding UTF8
    $cmContent = [System.Text.RegularExpressions.Regex]::Replace($cmContent, 'public const string MeterVersion = "[^"]+";', "public const string MeterVersion = `"$Version`";")
    Write-Utf8PreservingBom -Path $compelMetrics -Content $cmContent
    Write-SuccessLog "Updated CompelMetrics.cs -> MeterVersion $Version"
}

$cliRenderer = Join-Path $repoRoot "src\tools\headless-renderer\HeadlessUiRenderer.cs"
if (Test-Path -LiteralPath $cliRenderer) {
    $crContent = Get-Content -LiteralPath $cliRenderer -Raw -Encoding UTF8
    $crContent = [System.Text.RegularExpressions.Regex]::Replace($crContent, 'new CompelPingResponseDto \{\s*Application = "COMPEL",\s*Version = "[^"]+"', "new CompelPingResponseDto { Application = `"COMPEL`", Version = `"$Version`"")
    $crContent = [System.Text.RegularExpressions.Regex]::Replace($crContent, 'new CompelStatusResponseDto \{\s*Application = "COMPEL",\s*Version = "[^"]+"', "new CompelStatusResponseDto { Application = `"COMPEL`", Version = `"$Version`"")
    Write-Utf8PreservingBom -Path $cliRenderer -Content $crContent
    Write-SuccessLog "Updated HeadlessUiRenderer.cs -> COMPEL Version $Version"
}

# =========================================================================
# 6. UPDATE Client DXVK, News MOTD, Native Proxies, CITATION.cff & Tool Stamps
# =========================================================================
$citationPath = Join-Path $repoRoot "CITATION.cff"
if (Test-Path -LiteralPath $citationPath) {
    $citContent = Get-Content -LiteralPath $citationPath -Raw -Encoding UTF8
    $citContent = [System.Text.RegularExpressions.Regex]::Replace($citContent, '(?m)^version:\s*"[^"]+"', "version: `"$Version`"")
    $citContent = [System.Text.RegularExpressions.Regex]::Replace($citContent, '(?m)^date-released:\s*"[^"]+"', "date-released: `"$dashDate`"")
    Write-Utf8PreservingBom -Path $citationPath -Content $citContent
    Write-SuccessLog "Updated CITATION.cff -> Version $Version (date-released $dashDate)"
}

$dxvkFiles = @(
    (Join-Path $repoRoot "games\client\dxvk.conf"),
    (Join-Path $repoRoot "games\client\windows-x64\dxvk.conf")
)
foreach ($dxvkFile in $dxvkFiles) {
    if (Test-Path -LiteralPath $dxvkFile) {
        $dxContent = Get-Content -LiteralPath $dxvkFile -Raw -Encoding UTF8
        $dxContent = [System.Text.RegularExpressions.Regex]::Replace($dxContent, '# KONGOR_HOTFIX_STAMP=[^\r\n]+', "# KONGOR_HOTFIX_STAMP=v$Version")
        Write-Utf8PreservingBom -Path $dxvkFile -Content $dxContent
        Write-SuccessLog "Updated $(Split-Path $dxvkFile -Leaf) -> KONGOR_HOTFIX_STAMP=v$Version"
    }
}

$newsControllerPath = Join-Path $repoRoot "src\services\master-server\Controllers\News\NewsController.cs"
if (Test-Path -LiteralPath $newsControllerPath) {
    $ncContent = Get-Content -LiteralPath $newsControllerPath -Raw -Encoding UTF8
    $ncContent = [System.Text.RegularExpressions.Regex]::Replace($ncContent, '<div class="version-badge">v[^<]+ ACTIVE</div>', "<div class=`"version-badge`">v$Version ACTIVE</div>")
    Write-Utf8PreservingBom -Path $newsControllerPath -Content $ncContent
    Write-SuccessLog "Updated NewsController.cs -> MOTD Version Badge v$Version"
}

$scanDepsPath = Join-Path $repoRoot "scripts\dev\scan-dependencies.ps1"
if (Test-Path -LiteralPath $scanDepsPath) {
    $sdContent = Get-Content -LiteralPath $scanDepsPath -Raw -Encoding UTF8
    $sdContent = [System.Text.RegularExpressions.Regex]::Replace($sdContent, 'version = "[0-9\.]+"', "version = `"$Version`"")
    $sdContent = [System.Text.RegularExpressions.Regex]::Replace($sdContent, 'semanticVersion = "[0-9\.]+"', "semanticVersion = `"$Version`"")
    Write-Utf8PreservingBom -Path $scanDepsPath -Content $sdContent
    Write-SuccessLog "Updated scan-dependencies.ps1 -> Version $Version"
}

$nativeFiles = @(
    (Join-Path $repoRoot "native\proxies\native_guard\dllmain.cpp"),
    (Join-Path $repoRoot "native\drivers\vid_vulkan\src\vid_vulkan_spy.cpp"),
    (Join-Path $repoRoot "native\proxies\kongor_audio\fmod_audio_proxy.cpp")
)
foreach ($nFile in $nativeFiles) {
    if (Test-Path -LiteralPath $nFile) {
        $nContent = Get-Content -LiteralPath $nFile -Raw -Encoding UTF8
        $nContent = [System.Text.RegularExpressions.Regex]::Replace($nContent, 'Release:\s*v[0-9\.]+[^\s"]*', "Release: v$Version")
        $nContent = [System.Text.RegularExpressions.Regex]::Replace($nContent, 'Active patch:\s*v[0-9\.]+[^\s\()]*', "Active patch: v$Version")
        Write-Utf8PreservingBom -Path $nFile -Content $nContent
        Write-SuccessLog "Updated $(Split-Path $nFile -Leaf) -> Native release stamp v$Version"
    }
}

Write-SuccessLog "All ecosystem components, services, apps, and mod manifests successfully synchronized to Version $Version!"
