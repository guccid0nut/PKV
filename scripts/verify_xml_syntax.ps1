# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-flight XML syntax, K2 .entity semantic schema, and UI on*lua / icon validator.

.DESCRIPTION
    Validates XML well-formedness across .xml, .interface, .package, .entity, .effect, and XML-headed .mdf files,
    catches single-line '--' Lua comments inside UI on*lua attributes, enforces K2 .entity leaf-vs-branch combat
    action and binary-presence rules from docs/reference/k2_entity_xml_schema.json, and emits advisory warnings
    for non-power-of-two (NPOT) icon*.dds / icon*.png files in games/client/override/.

.EXAMPLE
    .\scripts\verify_xml_syntax.ps1
    .\scripts\verify_xml_syntax.ps1 -Path games/client/override/ui/fe2/store2.package
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$Path = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -Path (Join-Path -Path $PSScriptRoot -ChildPath "..")).Path
$schemaFile = Join-Path -Path $repoRoot -ChildPath "docs\reference\k2_entity_xml_schema.json"

$strictLeafSet = New-Object -TypeName 'System.Collections.Generic.HashSet[string]' -ArgumentList @([System.StringComparer]::OrdinalIgnoreCase)
$absentTagSet = New-Object -TypeName 'System.Collections.Generic.HashSet[string]' -ArgumentList @([System.StringComparer]::OrdinalIgnoreCase)

if (Test-Path -LiteralPath $schemaFile) {
    try {
        $schemaData = Get-Content -LiteralPath $schemaFile -Raw | ConvertFrom-Json
        if ($null -ne $schemaData.linter_rules -and $null -ne $schemaData.linter_rules.strict_leaf_tags_forbidding_children) {
            foreach ($leafTag in $schemaData.linter_rules.strict_leaf_tags_forbidding_children) {
                [void]$strictLeafSet.Add([string]$leafTag)
            }
        }
        if ($null -ne $schemaData.summary -and $null -ne $schemaData.summary.absent_in_build_forum_tags) {
            foreach ($absTag in $schemaData.summary.absent_in_build_forum_tags) {
                [void]$absentTagSet.Add([string]$absTag)
            }
        }
    } catch {
        Write-Host -Object " [FAIL] Corrupt K2 entity schema '$schemaFile': $($_.Exception.Message). Rebuild via: python scripts/assets/build_k2_entity_schema.py" -ForegroundColor Red
        exit 1
    }
}

function Test-IsXmlHeadedFile {
    param([string]$FilePath)
    $fs = [System.IO.File]::OpenRead($FilePath)
    try {
        $buf = New-Object -TypeName byte[] -ArgumentList @(64)
        $readLen = $fs.Read($buf, 0, $buf.Length)
        if ($readLen -le 0) { return $false }
        $idx = 0
        if ($readLen -ge 3 -and $buf[0] -eq 0xEF -and $buf[1] -eq 0xBB -and $buf[2] -eq 0xBF) {
            $idx = 3
        } elseif ($readLen -ge 2 -and $buf[0] -eq 0xFF -and $buf[1] -eq 0xFE) {
            $idx = 2
        }
        while ($idx -lt $readLen) {
            $b = $buf[$idx]
            if ($b -eq 0 -or $b -eq 9 -or $b -eq 10 -or $b -eq 13 -or $b -eq 32) {
                $idx++
                continue
            }
            return ($b -eq 0x3C)
        }
        return $false
    } finally {
        $fs.Dispose()
    }
}

function Test-EntitySemanticTree {
    param(
        [System.Xml.XmlElement]$Element,
        [bool]$IsRoot,
        [string]$DisplayPath,
        [System.Collections.Generic.List[string]]$ErrorList
    )
    $tagLower = $Element.Name.ToLowerInvariant()
    if ($absentTagSet.Contains($tagLower)) {
        $ErrorList.Add("[K2-ENTITY-ABSENT-TAG] <$($Element.Name)> in ${DisplayPath} is absent from games/client/game_shared_x64.dll in this K2 build. Check supported tags in docs/reference/k2_entity_xml_schema.json.")
    }

    $childElements = New-Object -TypeName 'System.Collections.Generic.List[System.Xml.XmlElement]'
    foreach ($childNode in $Element.ChildNodes) {
        if ($childNode -is [System.Xml.XmlElement]) {
            $childElements.Add([System.Xml.XmlElement]$childNode)
        }
    }

    if ((-not $IsRoot) -and $childElements.Count -gt 0 -and $strictLeafSet.Contains($tagLower)) {
        $firstChildName = $childElements[0].Name
        $ErrorList.Add("[K2-ENTITY-LEAF] <$($Element.Name)> in ${DisplayPath} is a leaf combat action (END_XML_PROCESSOR_NO_CHILDREN) and silently ignores child <$firstChildName>. Move child actions into a sibling or branch tag (<condition>, <chance>, <areaofeffect>) and re-run: .\scripts\verify_xml_syntax.ps1 -Path `"$DisplayPath`"")
    }

    foreach ($ce in $childElements) {
        Test-EntitySemanticTree -Element $ce -IsRoot $false -DisplayPath $DisplayPath -ErrorList $ErrorList
    }
}

$onLuaAttrRx = [regex]'(?i)\b(on\w*lua)\s*=\s*"([^"]*)"'
$onLuaCommentRx = [regex]'(?:\r?\n)\s*--[^\r\n]*(?:\r?\n)\s*[a-zA-Z]'

function Invoke-ValidateSingleXmlFile {
    param(
        [string]$FullPath,
        [System.Collections.Generic.List[string]]$ErrorList
    )
    $ext = [System.IO.Path]::GetExtension($FullPath).ToLowerInvariant()
    if ($ext -eq ".mdf" -and (-not (Test-IsXmlHeadedFile -FilePath $FullPath))) {
        return $false
    }

    $displayPath = if ($FullPath.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $FullPath.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    } else {
        $FullPath
    }

    $doc = New-Object -TypeName System.Xml.XmlDocument
    try {
        $doc.Load($FullPath)
    } catch {
        $ErrorList.Add("XML Parse Error in ${displayPath}: $($_.Exception.Message). Diagnose with: .\scripts\verify_xml_syntax.ps1 -Path `"$FullPath`"")
        return $true
    }

    if ($ext -in @(".xml", ".interface", ".package")) {
        $content = [System.IO.File]::ReadAllText($FullPath)
        foreach ($m in $onLuaAttrRx.Matches($content)) {
            $attrVal = $m.Groups[2].Value
            if ($onLuaCommentRx.IsMatch($attrVal)) {
                $ErrorList.Add("UI Lua Comment Error in ${displayPath}: single-line '--' Lua comment precedes active code in '$($m.Groups[1].Value)'. XML newline normalization collapses lines and comments out remaining code; use '--[[ ... ]]' block comments instead.")
            }
        }
    }

    if ($ext -eq ".entity" -and $null -ne $doc.DocumentElement -and ($strictLeafSet.Count -gt 0 -or $absentTagSet.Count -gt 0)) {
        Test-EntitySemanticTree -Element $doc.DocumentElement -IsRoot $true -DisplayPath $displayPath -ErrorList $ErrorList
    }

    return $true
}

function Invoke-CheckOverrideIconDimensions {
    param(
        [string]$OverrideRoot,
        [System.Collections.Generic.List[string]]$WarningList
    )
    if (-not (Test-Path -LiteralPath $OverrideRoot)) { return }
    foreach ($filePath in [System.IO.Directory]::EnumerateFiles($OverrideRoot, "icon*.*", [System.IO.SearchOption]::AllDirectories)) {
        $ext = [System.IO.Path]::GetExtension($filePath).ToLowerInvariant()
        if ($ext -notin @(".dds", ".png")) { continue }
        try {
            $fs = [System.IO.File]::OpenRead($filePath)
            try {
                $hdr = New-Object -TypeName byte[] -ArgumentList @(24)
                $n = $fs.Read($hdr, 0, 24)
                $w = 0
                $h = 0
                if ($ext -eq ".dds" -and $n -ge 20 -and $hdr[0] -eq 0x44 -and $hdr[1] -eq 0x44 -and $hdr[2] -eq 0x53 -and $hdr[3] -eq 0x20) {
                    $h = [System.BitConverter]::ToUInt32($hdr, 12)
                    $w = [System.BitConverter]::ToUInt32($hdr, 16)
                } elseif ($ext -eq ".png" -and $n -ge 24 -and $hdr[0] -eq 0x89 -and $hdr[1] -eq 0x50 -and $hdr[2] -eq 0x4E -and $hdr[3] -eq 0x47) {
                    $w = ([uint32]$hdr[16] -shl 24) -bor ([uint32]$hdr[17] -shl 16) -bor ([uint32]$hdr[18] -shl 8) -bor [uint32]$hdr[19]
                    $h = ([uint32]$hdr[20] -shl 24) -bor ([uint32]$hdr[21] -shl 16) -bor ([uint32]$hdr[22] -shl 8) -bor [uint32]$hdr[23]
                }
                if ($w -gt 0 -and $h -gt 0) {
                    $isPow2 = ((($w -band ($w - 1)) -eq 0) -and (($h -band ($h - 1)) -eq 0))
                    if (-not $isPow2) {
                        $relIcon = $filePath.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
                        $WarningList.Add("[ICON-NPOT] ${relIcon} (${w}x${h}) is non-power-of-two; K2 x64 downscales NPOT UI/item icons blurry (topic/1644). Resize canvas to power-of-two (e.g. 64x64 or 128x128).")
                    }
                }
            } finally {
                $fs.Dispose()
            }
        } catch {
            Write-Verbose -Message "Icon header inspection skipped for '$filePath': $($_.Exception.Message)"
        }
    }
}

function Invoke-ValidateUiTemplateLua {
    param(
        [string[]]$Interfaces,
        [System.Collections.Generic.List[string]]$ErrorList
    )
    $luacCandidates = @(
        (Join-Path $repoRoot "native\build\luac51_build\Release\luac51.exe"),
        (Join-Path $repoRoot "native\tools\luac51\build\Release\luac51.exe"),
        (Join-Path $repoRoot "artifacts\native-binaries\windows-x64\luac51.exe"),
        (Join-Path $repoRoot ".tools\bin\lua\luac51.exe")
    )
    $luacBin = $luacCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $luacBin) {
        Write-Host -Object " [SKIP] Template-expanded UI Lua check skipped (luac51.exe not built)." -ForegroundColor DarkGray
        return 0
    }

    $clientDir = Join-Path $repoRoot "games\client"
    if (-not (Test-Path -LiteralPath $clientDir -PathType Container)) {
        return 0
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (-not ("KongorUiTemplateLuaCollector" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

public class KongorUiSnippet {
    public string Code;
    public string Context;
}

public static class KongorUiTemplateLuaCollector {
    private static readonly Regex ParamRx = new Regex(@"\{([^{}]*)\}", RegexOptions.Compiled);

    private static string ResolveVfs(string vpath, string[] roots, ZipArchive z0, Dictionary<string, ZipArchiveEntry> z0Map) {
        string norm = vpath.TrimStart('/', '\\').Replace('\\', '/');
        foreach (string r in roots) {
            string p = Path.Combine(r, norm.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) {
                return File.ReadAllText(p, Encoding.UTF8);
            }
        }
        ZipArchiveEntry ze;
        if (z0 != null && z0Map.TryGetValue(norm.ToLowerInvariant(), out ze)) {
            using (var sr = new StreamReader(ze.Open(), Encoding.UTF8, true)) {
                return sr.ReadToEnd();
            }
        }
        return null;
    }

    private static void ScanIncludesAndTemplates(
        XmlElement node,
        string curDir,
        string vpath,
        string[] roots,
        ZipArchive z0,
        Dictionary<string, ZipArchiveEntry> z0Map,
        HashSet<string> visited,
        Dictionary<string, KeyValuePair<string, XmlElement>> templates,
        List<KeyValuePair<string, XmlElement>> rootsToWalk,
        List<string> errors)
    {
        foreach (XmlNode cn in node.ChildNodes) {
            var elem = cn as XmlElement;
            if (elem == null) continue;
            if (string.Equals(elem.Name, "include", StringComparison.OrdinalIgnoreCase)) {
                string inc = elem.GetAttribute("file");
                if (!string.IsNullOrEmpty(inc)) {
                    string incVpath = inc.StartsWith("/")
                        ? inc.TrimStart('/')
                        : (string.IsNullOrEmpty(curDir) ? inc : (curDir + "/" + inc));
                    LoadFile(incVpath, roots, z0, z0Map, visited, templates, rootsToWalk, errors);
                }
            } else if (string.Equals(elem.Name, "template", StringComparison.OrdinalIgnoreCase)) {
                string tname = elem.GetAttribute("name");
                if (!string.IsNullOrEmpty(tname)) {
                    templates[tname] = new KeyValuePair<string, XmlElement>(vpath, elem);
                }
            } else {
                ScanIncludesAndTemplates(elem, curDir, vpath, roots, z0, z0Map, visited, templates, rootsToWalk, errors);
            }
        }
    }

    private static void LoadFile(
        string vpath,
        string[] roots,
        ZipArchive z0,
        Dictionary<string, ZipArchiveEntry> z0Map,
        HashSet<string> visited,
        Dictionary<string, KeyValuePair<string, XmlElement>> templates,
        List<KeyValuePair<string, XmlElement>> rootsToWalk,
        List<string> errors)
    {
        string norm = vpath.TrimStart('/', '\\').Replace('\\', '/');
        if (!visited.Add(norm.ToLowerInvariant())) return;
        string content = ResolveVfs(norm, roots, z0, z0Map);
        if (content == null) return;
        var doc = new XmlDocument();
        try {
            doc.LoadXml(content);
        } catch (Exception ex) {
            errors.Add("UI Include XML Parse Error in " + norm + ": " + ex.Message);
            return;
        }
        if (doc.DocumentElement == null) return;
        int slash = norm.LastIndexOf('/');
        string curDir = slash >= 0 ? norm.Substring(0, slash) : "";
        ScanIncludesAndTemplates(doc.DocumentElement, curDir, norm, roots, z0, z0Map, visited, templates, rootsToWalk, errors);
        rootsToWalk.Add(new KeyValuePair<string, XmlElement>(norm, doc.DocumentElement));
    }

    private static string Subst(string text, Dictionary<string, string> args) {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
        string cur = text;
        for (int pass = 0; pass < 5; pass++) {
            string nxt = ParamRx.Replace(cur, m => {
                string inner = m.Groups[1].Value;
                int eq = inner.IndexOf('=');
                if (eq >= 0) {
                    string k = inner.Substring(0, eq);
                    string defVal = inner.Substring(eq + 1);
                    string v;
                    return (args != null && args.TryGetValue(k, out v)) ? v : defVal;
                }
                string val;
                return (args != null && args.TryGetValue(inner, out val)) ? val : "";
            });
            if (string.Equals(nxt, cur, StringComparison.Ordinal)) break;
            cur = nxt;
        }
        return cur;
    }

    private static void WalkEval(
        XmlElement elem,
        string vpath,
        Dictionary<string, KeyValuePair<string, XmlElement>> templates,
        Dictionary<string, string> args,
        bool inTemplate,
        int depth,
        Dictionary<string, string> uniqueSnippets)
    {
        if (depth > 25) return;
        if (string.Equals(elem.Name, "template", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(elem.Name, "instance", StringComparison.OrdinalIgnoreCase)) {
            string tname = elem.GetAttribute("name");
            var instArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlAttribute attr in elem.Attributes) {
                if (string.Equals(attr.Name, "name", StringComparison.OrdinalIgnoreCase)) continue;
                instArgs[attr.Name] = Subst(attr.Value, args);
            }
            KeyValuePair<string, XmlElement> tpl;
            if (!string.IsNullOrEmpty(tname) && templates.TryGetValue(tname, out tpl)) {
                string childCtx = tpl.Key + "->" + tname + "(" + vpath + ")";
                foreach (XmlNode cn in tpl.Value.ChildNodes) {
                    var ce = cn as XmlElement;
                    if (ce != null) {
                        WalkEval(ce, childCtx, templates, instArgs, true, depth + 1, uniqueSnippets);
                    }
                }
            }
            return;
        }

        foreach (XmlAttribute attr in elem.Attributes) {
            if (attr.Name.EndsWith("lua", StringComparison.OrdinalIgnoreCase)) {
                string code = inTemplate ? Subst(attr.Value, args) : attr.Value;
                if (!string.IsNullOrWhiteSpace(code) && !uniqueSnippets.ContainsKey(code)) {
                    uniqueSnippets[code] = vpath + " <" + elem.Name + " " + attr.Name + "=...>";
                }
            }
        }

        foreach (XmlNode cn in elem.ChildNodes) {
            var ce = cn as XmlElement;
            if (ce != null) {
                WalkEval(ce, vpath, templates, args, inTemplate, depth, uniqueSnippets);
            }
        }
    }

    public static List<KongorUiSnippet> Collect(string repoRoot, string[] interfaces, List<string> errors) {
        string[] roots = new string[] {
            Path.Combine(repoRoot, "games", "client", "override"),
            Path.Combine(repoRoot, "games", "client", "game"),
            Path.Combine(repoRoot, "games", "client", "ClientPackageFiles")
        };
        string z0Path = Path.Combine(repoRoot, "games", "client", "game", "resources0.s2z");
        ZipArchive z0 = null;
        var z0Map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var uniqueSnippets = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            if (File.Exists(z0Path)) {
                z0 = ZipFile.OpenRead(z0Path);
                foreach (var entry in z0.Entries) {
                    string norm = entry.FullName.TrimStart('/', '\\').Replace('\\', '/').ToLowerInvariant();
                    if (!z0Map.ContainsKey(norm)) z0Map[norm] = entry;
                }
            }
            foreach (string iface in interfaces) {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var templates = new Dictionary<string, KeyValuePair<string, XmlElement>>(StringComparer.Ordinal);
                var rootsToWalk = new List<KeyValuePair<string, XmlElement>>();
                LoadFile(iface, roots, z0, z0Map, visited, templates, rootsToWalk, errors);
                foreach (var kv in rootsToWalk) {
                    WalkEval(kv.Value, kv.Key, templates, null, false, 0, uniqueSnippets);
                }
            }
        } finally {
            if (z0 != null) z0.Dispose();
        }

        var result = new List<KongorUiSnippet>(uniqueSnippets.Count);
        foreach (var kv in uniqueSnippets) {
            result.Add(new KongorUiSnippet { Code = kv.Key, Context = kv.Value });
        }
        return result;
    }
}
"@ -ReferencedAssemblies @("System.Xml", "System.IO.Compression", "System.IO.Compression.FileSystem")
    }

    $snippets = [KongorUiTemplateLuaCollector]::Collect($repoRoot, $Interfaces, $ErrorList)
    if ($snippets.Count -eq 0) { return 0 }

    function Invoke-LuacBatchCheck {
        param(
            [KongorUiSnippet[]]$Batch,
            [string]$LuacExe
        )
        $sb = New-Object System.Text.StringBuilder
        for ($i = 0; $i -lt $Batch.Length; $i++) {
            [void]$sb.Append("local function _s").Append($i).Append("(...)`n")
            [void]$sb.Append($Batch[$i].Code).Append("`nend`n")
        }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $LuacExe
        $psi.Arguments = "-p -"
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $utf8Bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($sb.ToString())
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        try {
            $proc.StandardInput.BaseStream.Write($utf8Bytes, 0, $utf8Bytes.Length)
            $proc.StandardInput.BaseStream.Close()
        } catch [System.IO.IOException] {
            # luac51 exits immediately on the first syntax error and closes stdin before large batches finish writing.
        }
        $proc.WaitForExit()
        $stderr = $stderrTask.Result
        $null = $stdoutTask.Result
        if ($proc.ExitCode -eq 0) {
            return @()
        }
        if ($Batch.Length -eq 1) {
            $preview = ($Batch[0].Code -replace '[\r\n\t]+', ' ')
            if ($preview.Length -gt 140) { $preview = $preview.Substring(0, 140) + "..." }
            return @("[UI-TEMPLATE-LUA] $($Batch[0].Context) failed luac51 compile: $($stderr.Trim()) | snippet: $preview")
        }
        $mid = [int]($Batch.Length / 2)
        $left = Invoke-LuacBatchCheck -Batch $Batch[0..($mid - 1)] -LuacExe $LuacExe
        $right = Invoke-LuacBatchCheck -Batch $Batch[$mid..($Batch.Length - 1)] -LuacExe $LuacExe
        return @($left) + @($right)
    }

    $batchErrors = Invoke-LuacBatchCheck -Batch ($snippets.ToArray()) -LuacExe $luacBin
    foreach ($be in $batchErrors) {
        $ErrorList.Add($be)
    }
    return $snippets.Count
}

$errors = New-Object -TypeName 'System.Collections.Generic.List[string]'
$warnings = New-Object -TypeName 'System.Collections.Generic.List[string]'
$overrideExts = New-Object -TypeName 'System.Collections.Generic.HashSet[string]' -ArgumentList @([System.StringComparer]::OrdinalIgnoreCase)
foreach ($e in @(".xml", ".interface", ".package", ".entity", ".effect", ".mdf")) { [void]$overrideExts.Add($e) }

$uiExts = New-Object -TypeName 'System.Collections.Generic.HashSet[string]' -ArgumentList @([System.StringComparer]::OrdinalIgnoreCase)
foreach ($e in @(".xml", ".interface", ".package")) { [void]$uiExts.Add($e) }

if (-not [string]::IsNullOrWhiteSpace($Path)) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host -Object " [FAIL] Specified XML path does not exist: '$Path'. Pass a valid file or directory path to .\scripts\verify_xml_syntax.ps1 -Path <target>" -ForegroundColor Red
        exit 1
    }
    $resolvedTarget = (Resolve-Path -LiteralPath $Path).Path
    $targetItem = Get-Item -LiteralPath $resolvedTarget
    $validatedCount = 0
    if ($targetItem.PSIsContainer) {
        foreach ($candidate in [System.IO.Directory]::EnumerateFiles($resolvedTarget, "*.*", [System.IO.SearchOption]::AllDirectories)) {
            if ($overrideExts.Contains([System.IO.Path]::GetExtension($candidate))) {
                if (Invoke-ValidateSingleXmlFile -FullPath $candidate -ErrorList $errors) {
                    $validatedCount++
                }
            }
        }
        Invoke-CheckOverrideIconDimensions -OverrideRoot $resolvedTarget -WarningList $warnings
    } else {
        if (Invoke-ValidateSingleXmlFile -FullPath $resolvedTarget -ErrorList $errors) {
            $validatedCount++
        }
    }

    foreach ($w in $warnings) {
        Write-Host -Object " [WARN] $w" -ForegroundColor Yellow
    }
    if ($errors.Count -gt 0) {
        foreach ($err in $errors) {
            Write-Host -Object " [FAIL] $err" -ForegroundColor Red
        }
        exit 1
    }
    Write-Host -Object " [PASS] $Path passed K2 XML/entity/UI validation ($validatedCount file(s), 0 errors)!" -ForegroundColor Green
    exit 0
}

$totalValidated = 0
$overrideDir = Join-Path -Path $repoRoot -ChildPath "games\client\override"
if (Test-Path -LiteralPath $overrideDir) {
    foreach ($candidate in [System.IO.Directory]::EnumerateFiles($overrideDir, "*.*", [System.IO.SearchOption]::AllDirectories)) {
        if ($overrideExts.Contains([System.IO.Path]::GetExtension($candidate))) {
            if (Invoke-ValidateSingleXmlFile -FullPath $candidate -ErrorList $errors) {
                $totalValidated++
            }
        }
    }
    Invoke-CheckOverrideIconDimensions -OverrideRoot $overrideDir -WarningList $warnings
}

$cpfUiDir = Join-Path -Path $repoRoot -ChildPath "games\client\ClientPackageFiles\ui"
if (Test-Path -LiteralPath $cpfUiDir) {
    foreach ($candidate in [System.IO.Directory]::EnumerateFiles($cpfUiDir, "*.*", [System.IO.SearchOption]::AllDirectories)) {
        if ($uiExts.Contains([System.IO.Path]::GetExtension($candidate))) {
            if (Invoke-ValidateSingleXmlFile -FullPath $candidate -ErrorList $errors) {
                $totalValidated++
            }
        }
    }
}

$snippetCount = Invoke-ValidateUiTemplateLua -Interfaces @("ui/fe2/main_v2.interface", "ui/game_apex.interface") -ErrorList $errors

foreach ($w in $warnings) {
    Write-Host -Object " [WARN] $w" -ForegroundColor Yellow
}
Write-Host -Object "Total K2 XML/UI/Entity files validated: $totalValidated"
Write-Host -Object "Total template-expanded UI Lua snippets compiled (luac51): $snippetCount"
Write-Host -Object "Total validation errors: $($errors.Count)"
if ($errors.Count -gt 0) {
    foreach ($err in $errors) {
        Write-Host -Object " [FAIL] $err" -ForegroundColor Red
    }
    exit 1
}
Write-Host -Object "ALL K2 XML, PACKAGE & ENTITY FILES ARE 100% VALID!" -ForegroundColor Green
exit 0
