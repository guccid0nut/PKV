# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
# Project KONGOR V (https://github.com/guccidonut/PKV)

<#
.SYNOPSIS
    Linker-proof PE-header & binary firewall blocking dev-only profiler instrumentation (Tracy)
    from leaking into games/client, games/server, playtest bundles, or public exports.

.DESCRIPTION
    Uses compiled inline C# (compatible with both Windows PowerShell 5.1 / .NET Framework 4.8 and
    PowerShell 7 / .NET 8+) to inspect PE32/PE32+ headers in < 0.1 ms per binary (zero external
    dumpbin/objdump dependencies) and fails closed if:
      1. Any binary exports the dev-only marker symbol 'KongorDevOnlyTracyMarker' or '___tracy_*'.
      2. Any video renderer binary (vid_vulkan*.dll, vid_d3d12*.dll, vulkan_x64.dll) imports or
         delay-loads 'WS2_32.dll' or 'WSOCK32.dll' (required by Tracy's socket server, never imported
         by clean video drivers).
      3. Any non-PE native binary (.so / .dylib) contains the literal marker bytes
         'KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP'.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Paths = @(),

    [Parameter(Mandatory = $false)]
    [string]$RootDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ("KongorProfilerFirewallScanner" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class KongorProfilerFirewallScanner
{
    private static readonly byte[] MarkerBanner = Encoding.ASCII.GetBytes("KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP");

    private struct SectionInfo
    {
        public uint VirtualSize;
        public uint VirtualAddress;
        public uint SizeOfRawData;
        public uint PointerToRawData;
    }

    private static long RvaToFileOffset(uint rva, SectionInfo[] sections)
    {
        if (rva == 0) return -1L;
        for (int i = 0; i < sections.Length; i++)
        {
            ulong vAddr = sections[i].VirtualAddress;
            ulong vSpan = Math.Max((ulong)sections[i].VirtualSize, (ulong)sections[i].SizeOfRawData);
            if ((ulong)rva >= vAddr && (ulong)rva < (vAddr + vSpan))
            {
                ulong delta = (ulong)rva - vAddr;
                if (delta < (ulong)sections[i].SizeOfRawData)
                {
                    return (long)((ulong)sections[i].PointerToRawData + delta);
                }
                return -1L;
            }
        }
        return -1L;
    }

    private static string ReadAsciiZ(FileStream fs, long offset, int maxBytes)
    {
        if (offset < 0 || offset >= fs.Length) return string.Empty;
        fs.Position = offset;
        int cap = (int)Math.Min((long)maxBytes, fs.Length - offset);
        if (cap <= 0) return string.Empty;
        byte[] buf = new byte[cap];
        int read = fs.Read(buf, 0, cap);
        int end = 0;
        while (end < read && buf[end] != 0) end++;
        if (end == 0) return string.Empty;
        return Encoding.ASCII.GetString(buf, 0, end);
    }

    private static bool ContainsMarkerBanner(string filePath)
    {
        byte[] pat = MarkerBanner;
        int patLen = pat.Length;
        byte first = pat[0];
        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
        {
            if (fs.Length < patLen) return false;
            byte[] buf = new byte[65536 + patLen];
            int carry = 0;
            while (true)
            {
                int read = fs.Read(buf, carry, 65536);
                if (read <= 0) break;
                int total = carry + read;
                int limit = total - patLen;
                for (int i = 0; i <= limit; i++)
                {
                    if (buf[i] != first) continue;
                    bool match = true;
                    for (int j = 1; j < patLen; j++)
                    {
                        if (buf[i + j] != pat[j]) { match = false; break; }
                    }
                    if (match) return true;
                }
                carry = Math.Min(patLen - 1, total);
                Buffer.BlockCopy(buf, total - carry, buf, 0, carry);
            }
        }
        return false;
    }

    private static bool IsWinsockDll(string dllName)
    {
        return string.Equals(dllName, "WS2_32.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dllName, "WS2_32", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dllName, "WSOCK32.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dllName, "WSOCK32", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoDriverLeaf(string leaf)
    {
        if (leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return leaf.StartsWith("vid_vulkan", StringComparison.OrdinalIgnoreCase) ||
                   leaf.StartsWith("vid_d3d12", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(leaf, "vulkan_x64.dll", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(leaf, "vulkan_x64.exe", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static string[] InspectFile(string filePath)
    {
        List<string> reasons = new List<string>();
        string leaf = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath);

        if (string.Equals(ext, ".so", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".dylib", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsMarkerBanner(filePath))
            {
                reasons.Add("Contains dev-only profiler marker banner 'KONGOR_DEV_ONLY_TRACY_INSTRUMENTED_DO_NOT_SHIP'");
            }
            return reasons.ToArray();
        }

        bool isVideoDriver = IsVideoDriverLeaf(leaf);

        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
        using (BinaryReader br = new BinaryReader(fs, Encoding.ASCII, true))
        {
            if (fs.Length < 64) return reasons.ToArray();
            if (br.ReadUInt16() != 0x5A4D) return reasons.ToArray(); // 'MZ'

            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset < 64 || (peOffset + 24L) > fs.Length) return reasons.ToArray();

            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550u) return reasons.ToArray(); // "PE\0\0"

            br.ReadUInt16(); // Machine
            int numSections = br.ReadUInt16();
            br.ReadUInt32(); // TimeDateStamp
            br.ReadUInt32(); // PointerToSymbolTable
            br.ReadUInt32(); // NumberOfSymbols
            int sizeOfOptHeader = br.ReadUInt16();
            br.ReadUInt16(); // Characteristics

            long optHeaderStart = fs.Position;
            if (sizeOfOptHeader < 96 || (optHeaderStart + sizeOfOptHeader) > fs.Length) return reasons.ToArray();

            ushort optMagic = br.ReadUInt16();
            long dataDirOffset;
            if (optMagic == 0x010B) dataDirOffset = optHeaderStart + 96;
            else if (optMagic == 0x020B) dataDirOffset = optHeaderStart + 112;
            else return reasons.ToArray();

            fs.Position = dataDirOffset - 4;
            int numRvaAndSizes = (int)Math.Min(16u, br.ReadUInt32());

            uint exportRva = 0, importRva = 0, delayImportRva = 0;
            if (numRvaAndSizes >= 1) { exportRva = br.ReadUInt32(); br.ReadUInt32(); }
            if (numRvaAndSizes >= 2) { importRva = br.ReadUInt32(); br.ReadUInt32(); }
            if (numRvaAndSizes >= 14)
            {
                fs.Position = dataDirOffset + (13 * 8);
                delayImportRva = br.ReadUInt32();
            }

            if (exportRva == 0 && !isVideoDriver)
            {
                return reasons.ToArray();
            }

            long sectionTableStart = optHeaderStart + sizeOfOptHeader;
            if ((sectionTableStart + (numSections * 40L)) > fs.Length) return reasons.ToArray();

            SectionInfo[] sections = new SectionInfo[numSections];
            for (int si = 0; si < numSections; si++)
            {
                fs.Position = sectionTableStart + (si * 40L) + 8;
                sections[si].VirtualSize = br.ReadUInt32();
                sections[si].VirtualAddress = br.ReadUInt32();
                sections[si].SizeOfRawData = br.ReadUInt32();
                sections[si].PointerToRawData = br.ReadUInt32();
            }

            // 1. Export Directory check
            if (exportRva != 0)
            {
                long expOff = RvaToFileOffset(exportRva, sections);
                if (expOff >= 0 && (expOff + 40L) <= fs.Length)
                {
                    fs.Position = expOff + 24;
                    int numNames = (int)Math.Min(65536u, br.ReadUInt32());
                    br.ReadUInt32(); // AddressOfFunctions
                    uint addrOfNamesRva = br.ReadUInt32();
                    long namesArrayOff = RvaToFileOffset(addrOfNamesRva, sections);
                    if (namesArrayOff >= 0 && (namesArrayOff + (numNames * 4L)) <= fs.Length)
                    {
                        for (int ni = 0; ni < numNames; ni++)
                        {
                            fs.Position = namesArrayOff + (ni * 4L);
                            uint symRva = br.ReadUInt32();
                            long symOff = RvaToFileOffset(symRva, sections);
                            if (symOff >= 0)
                            {
                                string symName = ReadAsciiZ(fs, symOff, 128);
                                if (string.Equals(symName, "KongorDevOnlyTracyMarker", StringComparison.OrdinalIgnoreCase) ||
                                    symName.StartsWith("___tracy_", StringComparison.OrdinalIgnoreCase))
                                {
                                    reasons.Add("PE Export Directory exports dev-only profiler symbol '" + symName + "'");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 2. Import & Delay-Load Import Directory check on video drivers
            if (isVideoDriver)
            {
                if (importRva != 0)
                {
                    long impOff = RvaToFileOffset(importRva, sections);
                    for (int di = 0; impOff >= 0 && (impOff + 20L) <= fs.Length && di < 1024; di++, impOff += 20L)
                    {
                        fs.Position = impOff;
                        uint oft = br.ReadUInt32();
                        uint tds = br.ReadUInt32();
                        uint fwd = br.ReadUInt32();
                        uint dllNameRva = br.ReadUInt32();
                        uint ft = br.ReadUInt32();
                        if (oft == 0 && tds == 0 && fwd == 0 && dllNameRva == 0 && ft == 0) break;
                        if (dllNameRva != 0)
                        {
                            long dllOff = RvaToFileOffset(dllNameRva, sections);
                            if (dllOff >= 0)
                            {
                                string dllName = ReadAsciiZ(fs, dllOff, 128);
                                if (IsWinsockDll(dllName))
                                {
                                    reasons.Add("Video renderer PE Import Directory links forbidden socket library '" + dllName + "' (Tracy socket telemetry signature)");
                                }
                            }
                        }
                    }
                }

                if (delayImportRva != 0)
                {
                    long dlyOff = RvaToFileOffset(delayImportRva, sections);
                    for (int ddi = 0; dlyOff >= 0 && (dlyOff + 32L) <= fs.Length && ddi < 256; ddi++, dlyOff += 32L)
                    {
                        fs.Position = dlyOff;
                        uint grAttrs = br.ReadUInt32();
                        uint rvaDllName = br.ReadUInt32();
                        if (rvaDllName == 0) break;
                        if ((grAttrs & 1u) != 0)
                        {
                            long dllOff = RvaToFileOffset(rvaDllName, sections);
                            if (dllOff >= 0)
                            {
                                string dllName = ReadAsciiZ(fs, dllOff, 128);
                                if (IsWinsockDll(dllName))
                                {
                                    reasons.Add("Video renderer PE Delay-Load Import Directory links forbidden socket library '" + dllName + "'");
                                }
                            }
                        }
                    }
                }
            }
        }

        return reasons.ToArray();
    }
}
"@
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$candidateFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Add-CandidateTree {
    param([string]$TargetPath)
    if ([string]::IsNullOrWhiteSpace($TargetPath)) { return }
    if (-not (Test-Path -LiteralPath $TargetPath)) { return }

    $item = Get-Item -LiteralPath $TargetPath -Force
    if (-not $item.PSIsContainer) {
        [void]$candidateFiles.Add($item.FullName)
        return
    }

    $gamesSubdir = Join-Path $item.FullName "games"
    $isRepoRootLayout = (Test-Path -LiteralPath (Join-Path $item.FullName "ProjectKongor.slnx") -PathType Leaf) -and
                        (Test-Path -LiteralPath $gamesSubdir -PathType Container)

    $scanRoots = if ($isRepoRootLayout) {
        @(
            (Join-Path $item.FullName "games\client"),
            (Join-Path $item.FullName "games\client\windows-x64"),
            (Join-Path $item.FullName "games\client\linux-x64"),
            (Join-Path $item.FullName "games\server"),
            (Join-Path $item.FullName "games\server\windows-x64"),
            (Join-Path $item.FullName "games\server\linux-x64")
        )
    } else {
        @($item.FullName)
    }

    foreach ($sr in $scanRoots) {
        if (-not (Test-Path -LiteralPath $sr -PathType Container)) { continue }
        if ($isRepoRootLayout) {
            foreach ($f in (Get-ChildItem -LiteralPath $sr -File -Force -ErrorAction SilentlyContinue)) {
                if ($f.Extension -match '^(?i)\.(dll|exe|so|dylib)$') {
                    [void]$candidateFiles.Add($f.FullName)
                }
            }
        } else {
            foreach ($f in (Get-ChildItem -LiteralPath $sr -Recurse -File -Force -ErrorAction SilentlyContinue)) {
                if ($f.Extension -match '^(?i)\.(dll|exe|so|dylib)$') {
                    [void]$candidateFiles.Add($f.FullName)
                }
            }
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($RootDir)) {
    Add-CandidateTree -TargetPath $RootDir
}
foreach ($p in $Paths) {
    Add-CandidateTree -TargetPath $p
}

$violations = New-Object System.Collections.Generic.List[string]
foreach ($filePath in $candidateFiles) {
    $fileReasons = [KongorProfilerFirewallScanner]::InspectFile($filePath)
    foreach ($r in $fileReasons) {
        $violations.Add("  - ${filePath}: $r")
    }
}

$sw.Stop()
if ($violations.Count -gt 0) {
    $joined = ($violations -join [Environment]::NewLine)
    $errMsg = @(
        "[PROFILER-LEAK-FIREWALL] BLOCKED dev-only Tracy profiler instrumentation in $($violations.Count) binary check(s) ($($sw.ElapsedMilliseconds) ms):",
        $joined,
        "How to fix: Rebuild and deploy clean Release vid_vulkan_x64.dll without Tracy:",
        "  .\scripts\Invoke-KongorTestLock.ps1 -Owner clean-vulkan -- cmake -S native -B native/build -DKONGOR_ENABLE_TRACY=OFF",
        "  .\scripts\Invoke-KongorTestLock.ps1 -Owner clean-vulkan -- cmake --build native/build --config Release --target vid_vulkan_x64"
    ) -join [Environment]::NewLine
    throw $errMsg
}

if (-not $Quiet) {
    Write-Host "[PROFILER-LEAK-FIREWALL] PASS: Scanned $($candidateFiles.Count) native binaries in $($sw.ElapsedMilliseconds) ms (0 dev-profiler imports/exports)." -ForegroundColor Green
}
