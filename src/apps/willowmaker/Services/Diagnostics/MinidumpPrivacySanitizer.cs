// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MinidumpPrivacySanitizer.cs
// System: WILLOWMAKER - Diagnostics & Privacy Firewall
// Subsystem: Allowlist-Driven Win32 MDMP Structural Sanitizer & Stack Wiper
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Kongor.Core.Diagnostics;

namespace WILLOWMAKER.Core.Services.Diagnostics;

public sealed record MinidumpSanitizeResult(
    bool AllowedForExport,
    string StatusCode,
    string Reason,
    int StreamsRemoved,
    int ModulesSanitized,
    int BackgroundStacksCleared,
    int StackTextBytesWiped);

/// <summary>
/// Structurally sanitizes a Win32 MDMP binary minidump in-place before zip export:
/// 1. Rejects full-memory dumps (Memory64ListStream=9, MemoryInfoListStream=16, MiniDumpWithFullMemory).
/// 2. Enforces a strict stream allowlist (3, 4, 5, 6, 7, 14, 15, 17), zero-filling and removing all other streams
///    (such as FunctionTableStream=13 heap-slack descriptors, HandleDataStream=12, TokenStream=19, SystemMemoryInfoStream=21, CommentStreamA/W=10/11).
/// 3. Rewrites ModuleListStream (4) &amp; UnloadedModuleListStream (14) paths in-place to basenames only,
///    anonymizes non-faulting/non-OS/non-Kongor 3rd-party modules to third_party_module_N.dll, and strips PDB paths.
/// 4. Clears non-faulting background thread stacks and wipes all contiguous printable ASCII/UTF-16LE text runs
///    from the single retained stack while preserving 8-byte x64 code return addresses and stack frame pointers.
/// </summary>
public static class MinidumpPrivacySanitizer
{
    private const uint StreamThreadList = 3u;
    private const uint StreamModuleList = 4u;
    private const uint StreamMemoryList = 5u;
    private const uint StreamException = 6u;
    private const uint StreamSystemInfo = 7u;
    private const uint StreamMemory64List = 9u;
    // Note: StreamFunctionTable (13u) is intentionally excluded because Windows 11 dbgcore.dll copies
    // unzeroed freed process heap slack into dynamic function table descriptors; x64 PE unwind uses static .pdata.
    private const uint StreamUnloadedModuleList = 14u;
    private const uint StreamMiscInfo = 15u;
    private const uint StreamMemoryInfoList = 16u;
    private const uint StreamThreadInfoList = 17u;

    private const ulong MiniDumpWithFullMemoryFlag = 0x00000002UL;

    private static readonly HashSet<uint> AllowedStreams =
    [
        StreamThreadList,
        StreamModuleList,
        StreamMemoryList,
        StreamException,
        StreamSystemInfo,
        StreamUnloadedModuleList,
        StreamMiscInfo,
        StreamThreadInfoList
    ];

    private static readonly HashSet<string> SafeKnownEngineAndSystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "hon_x64.exe", "hon.exe", "hon-x86_64", "willowmaker.exe",
        "k2_x64.dll", "k2.dll", "cgame_x64.dll", "cgame.dll", "game_x64.dll", "game.dll",
        "vid_d3d9_x64.dll", "vid_d3d9.dll", "vid_gl2_x64.dll", "vid_gl2.dll", "vid_vulkan_x64.dll",
        "d3d9.dll", "dxgi.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll", "opengl32.dll",
        "hon_vulkan_layer.dll", "native_guard.dll", "version.dll", "winhttp.dll",
        "fmodex64.dll", "fmodex.dll", "openal32.dll", "soft_oal.dll",
        "lua5.1.dll", "lua51.dll", "zlib1.dll", "libcurl.dll", "curl_x64.dll", "freetype6.dll", "speex.dll",
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "user32.dll", "win32u.dll", "gdi32.dll", "gdi32full.dll",
        "msvcrt.dll", "ucrtbase.dll", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll",
        "advapi32.dll", "sechost.dll", "rpcrt4.dll", "combase.dll", "ole32.dll", "oleaut32.dll",
        "shell32.dll", "shcore.dll", "shlwapi.dll", "imm32.dll", "ws2_32.dll", "mswsock.dll",
        "iphlpapi.dll", "dnsapi.dll", "crypt32.dll", "bcrypt.dll", "bcryptprimitives.dll",
        "setupapi.dll", "cfgmgr32.dll", "dwmapi.dll", "uxtheme.dll", "winmm.dll", "mmdevapi.dll", "audioses.dll",
        "dbghelp.dll", "dbgcore.dll", "powrprof.dll", "umpdc.dll", "kernel.appcore.dll",
        "nvoglv64.dll", "nvwgf2umx.dll", "nvldumdx.dll", "nvgpucomp64.dll", "nvspcap64.dll",
        "amdvlk64.dll", "amdxn64.dll", "aticfx64.dll", "amdihk64.dll",
        "igvk64.dll", "igd10iumd64.dll", "igd12umd64.dll", "igdgmm64.dll"
    };

    public static bool IsSafeEngineOrSystemModule(string? rawModulePath, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        if (SafeKnownEngineAndSystemModules.Contains(baseName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rawModulePath))
        {
            string normalized = rawModulePath.Replace('/', '\\');
            if (normalized.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(@"\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static MinidumpSanitizeResult SanitizeInPlace(
        byte[] buffer,
        PrivacyScrubContext? scrubContext = null,
        IEnumerable<string?>? extraSensitiveValues = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length < 32)
        {
            Trace.WriteLine("[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: buffer shorter than 32-byte MDMP header.");
            return new MinidumpSanitizeResult(false, "INVALID_HEADER", "Minidump shorter than 32-byte MDMP header.", 0, 0, 0, 0);
        }

        Span<byte> span = buffer.AsSpan();
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        if (signature != MinidumpReader.MdmpSignature)
        {
            Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: invalid signature 0x{signature:X8}.");
            return new MinidumpSanitizeResult(false, "INVALID_SIGNATURE", "File does not carry a valid MDMP signature.", 0, 0, 0, 0);
        }

        uint streamCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        uint streamDirRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        ulong dumpFlags = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8));

        if ((dumpFlags & MiniDumpWithFullMemoryFlag) != 0)
        {
            Trace.WriteLine("[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: MiniDumpWithFullMemory flag set.");
            return new MinidumpSanitizeResult(false, "FORBIDDEN_FULL_MEMORY_FLAG", "Full-memory minidumps are never exported in shareable bundles.", 0, 0, 0, 0);
        }

        if (streamCount == 0 || streamCount > 256 || streamDirRva < 32 || (ulong)streamDirRva + ((ulong)streamCount * 12UL) > (ulong)buffer.Length)
        {
            Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: invalid stream directory (count={streamCount}, rva={streamDirRva}).");
            return new MinidumpSanitizeResult(false, "CORRUPT_DIRECTORY", "Minidump directory header bounds are invalid.", 0, 0, 0, 0);
        }

        // Zero header CheckSum
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 0u);

        List<(uint Type, uint Size, uint Rva)> rawDirs = new((int)streamCount);
        for (int i = 0; i < streamCount; i++)
        {
            int entryOff = (int)streamDirRva + (i * 12);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 4, 4));
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 8, 4));

            if (type == StreamMemory64List)
            {
                Trace.WriteLine("[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: contains forbidden Memory64ListStream (9).");
                return new MinidumpSanitizeResult(false, "FORBIDDEN_MEMORY64_STREAM", "Full-heap Memory64ListStream minidumps are blocked from export.", 0, 0, 0, 0);
            }

            if (type == StreamMemoryInfoList)
            {
                Trace.WriteLine("[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: contains forbidden MemoryInfoListStream (16).");
                return new MinidumpSanitizeResult(false, "FORBIDDEN_MEMORY_INFO_STREAM", "Process virtual-memory layout streams are blocked from export.", 0, 0, 0, 0);
            }

            if (type != 0 && size > 0 && (rva < 32 || (ulong)rva + size > (ulong)buffer.Length))
            {
                Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] Rejected minidump: out-of-bounds RVA on stream {type} (rva={rva}, size={size}).");
                return new MinidumpSanitizeResult(false, "CORRUPT_STREAM_BOUNDS", $"Stream {type} RVA exceeds minidump bounds.", 0, 0, 0, 0);
            }

            rawDirs.Add((type, size, rva));
        }

        // Collect allowed ranges first so we never zero-fill overlapping shared regions
        List<(uint Start, uint End)> allowedRanges = new();
        List<(uint Type, uint Size, uint Rva)> keptDirs = new();
        List<(uint Start, uint End)> rejectedRanges = new();
        int streamsRemoved = 0;

        foreach ((uint type, uint size, uint rva) in rawDirs)
        {
            if (type == 0)
            {
                streamsRemoved++;
                continue;
            }

            if (AllowedStreams.Contains(type))
            {
                keptDirs.Add((type, size, rva));
                if (size > 0)
                {
                    allowedRanges.Add((rva, rva + size));
                }
            }
            else
            {
                streamsRemoved++;
                if (size > 0 && rva >= 32)
                {
                    rejectedRanges.Add((rva, rva + size));
                }
            }
        }

        // Zero-fill rejected stream payloads byte-by-byte where they don't intersect kept streams
        foreach ((uint rStart, uint rEnd) in rejectedRanges)
        {
            for (uint pos = rStart; pos < rEnd; pos++)
            {
                bool inAllowed = false;
                for (int a = 0; a < allowedRanges.Count; a++)
                {
                    if (pos >= allowedRanges[a].Start && pos < allowedRanges[a].End)
                    {
                        inAllowed = true;
                        break;
                    }
                }
                if (!inAllowed)
                {
                    span[(int)pos] = 0;
                }
            }
        }

        // Rewrite compacted directory table and zero trailing slots
        span.Slice((int)streamDirRva, (int)(streamCount * 12)).Clear();
        for (int i = 0; i < keptDirs.Count; i++)
        {
            int entryOff = (int)streamDirRva + (i * 12);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff, 4), keptDirs[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 4, 4), keptDirs[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 8, 4), keptDirs[i].Rva);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), (uint)keptDirs.Count);

        uint? preNumberOfThreads = TryReadStreamListCount(span, keptDirs, StreamThreadList);
        uint? preNumberOfModules = TryReadStreamListCount(span, keptDirs, StreamModuleList);

        // 1. Sanitize Stream 7 (SystemInfoStream): clear CSDVersionRva string if present
        int sysInfoIdx = keptDirs.FindIndex(d => d.Type == StreamSystemInfo);
        if (sysInfoIdx >= 0 && keptDirs[sysInfoIdx].Size >= 28)
        {
            uint csdRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)keptDirs[sysInfoIdx].Rva + 24, 4));
            if (csdRva >= 32 && (ulong)csdRva + 4 <= (ulong)buffer.Length)
            {
                uint csdOrigLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)csdRva, 4));
                Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] SystemInfoStream.CSDVersionRva=0x{csdRva:X}, origByteLen={csdOrigLen}.");
                OverwriteMinidumpStringInPlace(span, csdRva, string.Empty);
            }
        }

        // 2. Sanitize Stream 15 (MiscInfoStream): truncate to 24B (ProcessId + ProcessTimes only), wiping timezone/build strings
        int miscIdx = keptDirs.FindIndex(d => d.Type == StreamMiscInfo);
        if (miscIdx >= 0 && keptDirs[miscIdx].Size > 24)
        {
            uint mRva = keptDirs[miscIdx].Rva;
            uint mSize = keptDirs[miscIdx].Size;
            uint flags1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)mRva + 4, 4)) & 0x00000003u;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice((int)mRva, 4), 24u);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice((int)mRva + 4, 4), flags1);
            span.Slice((int)mRva + 24, (int)(mSize - 24)).Clear();

            int dirOff = (int)streamDirRva + (miscIdx * 12);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(dirOff + 4, 4), 24u);
            keptDirs[miscIdx] = (StreamMiscInfo, 24u, mRva);
        }

        // 3. Extract faulting thread ID and faulting address from Stream 6 (ExceptionStream)
        uint crashingThreadId = 0;
        ulong faultAddress = 0;
        int excIdx = keptDirs.FindIndex(d => d.Type == StreamException);
        if (excIdx >= 0 && keptDirs[excIdx].Size >= 32)
        {
            uint eRva = keptDirs[excIdx].Rva;
            crashingThreadId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)eRva, 4));
            faultAddress = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice((int)eRva + 24, 8));

            if (faultAddress == 0 && keptDirs[excIdx].Size >= 168)
            {
                uint ctxSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)eRva + 160, 4));
                uint ctxRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)eRva + 164, 4));
                if (ctxRva >= 32 && ctxSize >= 0x100 && (ulong)ctxRva + 0x100 <= (ulong)buffer.Length)
                {
                    faultAddress = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice((int)ctxRva + 0xF8, 8)); // RIP
                }
            }
        }

        // 4. Sanitize Stream 4 (ModuleListStream) & Stream 13 (UnloadedModuleListStream)
        List<(ulong Start, ulong End)> validCodeRanges = new();
        int modulesSanitized = 0;
        int localThirdPartyCounter = 0;

        int modIdx = keptDirs.FindIndex(d => d.Type == StreamModuleList);
        if (modIdx >= 0 && keptDirs[modIdx].Size >= 4)
        {
            uint modRva = keptDirs[modIdx].Rva;
            uint numMods = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)modRva, 4));
            for (int m = 0; m < numMods && (ulong)modRva + 4UL + (((ulong)m + 1UL) * 108UL) <= (ulong)buffer.Length; m++)
            {
                int entryOff = (int)modRva + 4 + (m * 108);
                ulong baseOfImage = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOff, 8));
                uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 8, 4));
                uint modNameRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 20, 4));
                uint cvSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 76, 4));
                uint cvRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 80, 4));
                uint miscRecSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 84, 4));
                uint miscRecRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(entryOff + 88, 4));

                if (sizeOfImage > 0)
                {
                    validCodeRanges.Add((baseOfImage, baseOfImage + sizeOfImage));
                }

                bool isFaulting = faultAddress != 0 && faultAddress >= baseOfImage && faultAddress < baseOfImage + sizeOfImage;
                bool isAnonymizedThirdParty = false;

                if (modNameRva >= 32 && (ulong)modNameRva + 4 <= (ulong)buffer.Length)
                {
                    string rawPath = ReadMinidumpString(span, modNameRva);
                    string baseName = ExtractCrossPlatformFileName(rawPath);
                    if (string.IsNullOrWhiteSpace(baseName))
                    {
                        baseName = "module.dll";
                    }

                    string targetName;
                    if (isFaulting || IsSafeEngineOrSystemModule(rawPath, baseName))
                    {
                        targetName = baseName;
                    }
                    else
                    {
                        isAnonymizedThirdParty = true;
                        targetName = scrubContext != null
                            ? scrubContext.MapThirdPartyModuleName(baseName)
                            : $"third_party_module_{++localThirdPartyCounter}.dll";
                    }

                    OverwriteMinidumpStringInPlace(span, modNameRva, targetName);
                    modulesSanitized++;
                }

                // Clear MiscRecord if present
                if (miscRecSize > 0 && miscRecRva >= 32 && (ulong)miscRecRva + miscRecSize <= (ulong)buffer.Length)
                {
                    span.Slice((int)miscRecRva, (int)miscRecSize).Clear();
                    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 84, 4), 0u);
                    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 88, 4), 0u);
                }

                // Sanitize CodeView RSDS PDB path
                if (cvSize > 0 && cvRva >= 32 && (ulong)cvRva + cvSize <= (ulong)buffer.Length)
                {
                    if (isAnonymizedThirdParty)
                    {
                        span.Slice((int)cvRva, (int)cvSize).Clear();
                        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 76, 4), 0u);
                        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 80, 4), 0u);
                    }
                    else if (cvSize > 24 &&
                             span[(int)cvRva] == (byte)'R' &&
                             span[(int)cvRva + 1] == (byte)'S' &&
                             span[(int)cvRva + 2] == (byte)'D' &&
                             span[(int)cvRva + 3] == (byte)'S')
                    {
                        int pdbPathLen = (int)cvSize - 24;
                        string rawPdb = Encoding.UTF8.GetString(span.Slice((int)cvRva + 24, pdbPathLen)).TrimEnd('\0');
                        string pdbBase = ExtractCrossPlatformFileName(rawPdb);
                        span.Slice((int)cvRva + 24, pdbPathLen).Clear();
                        byte[] pdbBaseBytes = Encoding.UTF8.GetBytes(pdbBase);
                        int copyLen = Math.Min(pdbBaseBytes.Length, Math.Max(0, pdbPathLen - 1));
                        pdbBaseBytes.AsSpan(0, copyLen).CopyTo(span.Slice((int)cvRva + 24, copyLen));
                        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 76, 4), (uint)(24 + copyLen + 1));
                    }
                    else
                    {
                        span.Slice((int)cvRva, (int)cvSize).Clear();
                        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(entryOff + 76, 4), 0u);
                    }
                }
            }
        }

        int unloadedIdx = keptDirs.FindIndex(d => d.Type == StreamUnloadedModuleList);
        if (unloadedIdx >= 0 && keptDirs[unloadedIdx].Size >= 12)
        {
            uint uRva = keptDirs[unloadedIdx].Rva;
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)uRva, 4));
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)uRva + 4, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)uRva + 8, 4));
            if (headerSize >= 12 && entrySize >= 24)
            {
                for (uint u = 0; u < count && (ulong)uRva + headerSize + ((u + 1UL) * entrySize) <= (ulong)buffer.Length; u++)
                {
                    int uOff = (int)(uRva + headerSize + (u * entrySize));
                    uint uNameRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(uOff + 16, 4));
                    if (uNameRva >= 32 && (ulong)uNameRva + 4 <= (ulong)buffer.Length)
                    {
                        string rawName = ReadMinidumpString(span, uNameRva);
                        string baseName = ExtractCrossPlatformFileName(rawName);
                        string safeName = IsSafeEngineOrSystemModule(rawName, baseName)
                            ? baseName
                            : (scrubContext != null ? scrubContext.MapThirdPartyModuleName(baseName) : $"third_party_module_{++localThirdPartyCounter}.dll");
                        OverwriteMinidumpStringInPlace(span, uNameRva, safeName);
                        modulesSanitized++;
                    }
                }
            }
        }

        // 5. Filter Stream 3 (ThreadList) & Stream 5 (MemoryList) to retain ONLY the crashing (or main) thread stack
        ulong retainedStackStart = 0;
        ulong retainedStackEnd = 0;
        int backgroundStacksCleared = 0;

        int threadIdx = keptDirs.FindIndex(d => d.Type == StreamThreadList);
        if (threadIdx >= 0 && keptDirs[threadIdx].Size >= 4)
        {
            uint tRva = keptDirs[threadIdx].Rva;
            uint numThreads = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)tRva, 4));
            uint targetTid = crashingThreadId;

            if (targetTid == 0 && numThreads > 0 && (ulong)tRva + 52UL <= (ulong)buffer.Length)
            {
                targetTid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)tRva + 4, 4));
            }

            for (int t = 0; t < numThreads && (ulong)tRva + 4UL + (((ulong)t + 1UL) * 48UL) <= (ulong)buffer.Length; t++)
            {
                int tOff = (int)tRva + 4 + (t * 48);
                uint tid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(tOff, 4));
                ulong stackStart = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(tOff + 24, 8));
                uint stackDataSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(tOff + 32, 4));
                uint stackDataRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(tOff + 36, 4));

                // Zero TEB pointer so PEB/TEB user environment hints are not advertised
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(tOff + 16, 8), 0UL);

                if (tid == targetTid && retainedStackEnd == 0 && stackDataSize > 0)
                {
                    retainedStackStart = stackStart;
                    retainedStackEnd = stackStart + stackDataSize;
                }
                else
                {
                    if (stackDataSize > 0 && stackDataRva >= 32 && (ulong)stackDataRva + stackDataSize <= (ulong)buffer.Length)
                    {
                        span.Slice((int)stackDataRva, (int)stackDataSize).Clear();
                        backgroundStacksCleared++;
                    }
                    BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(tOff + 24, 8), 0UL);
                    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tOff + 32, 4), 0u);
                    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(tOff + 36, 4), 0u);
                }
            }
        }

        // 6. Pointer-preserving x64 stack text wiper on Stream 5 (MemoryListStream)
        int stackTextBytesWiped = 0;
        int memIdx = keptDirs.FindIndex(d => d.Type == StreamMemoryList);
        if (memIdx >= 0 && keptDirs[memIdx].Size >= 4)
        {
            uint memRva = keptDirs[memIdx].Rva;
            uint numRanges = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)memRva, 4));
            List<(ulong StartAddr, uint DataSize, uint DataRva)> keptMemRanges = new();

            for (int r = 0; r < numRanges && (ulong)memRva + 4UL + (((ulong)r + 1UL) * 16UL) <= (ulong)buffer.Length; r++)
            {
                int rOff = (int)memRva + 4 + (r * 16);
                ulong startAddr = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(rOff, 8));
                uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(rOff + 8, 4));
                uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(rOff + 12, 4));

                if (dataSize == 0 || dataRva < 32 || (ulong)dataRva + dataSize > (ulong)buffer.Length)
                {
                    continue;
                }

                bool belongsToRetained = retainedStackEnd == 0
                    ? keptMemRanges.Count == 0
                    : (startAddr < retainedStackEnd && (startAddr + dataSize) > retainedStackStart);

                if (!belongsToRetained)
                {
                    span.Slice((int)dataRva, (int)dataSize).Clear();
                    backgroundStacksCleared++;
                    continue;
                }

                stackTextBytesWiped += WipeStackPrintableTextPreservingPointers(
                    span.Slice((int)dataRva, (int)dataSize),
                    validCodeRanges,
                    retainedStackStart != 0 ? retainedStackStart : startAddr,
                    retainedStackEnd != 0 ? retainedStackEnd : (startAddr + dataSize));

                keptMemRanges.Add((startAddr, dataSize, dataRva));
            }

            // Rewrite compacted MemoryListStream and sync MINIDUMP_DIRECTORY DataSize (4 + Count * 16) for WinDbg dbgeng
            uint compactedMemSize = (uint)(4 + (keptMemRanges.Count * 16));
            span.Slice((int)memRva, (int)keptDirs[memIdx].Size).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice((int)memRva, 4), (uint)keptMemRanges.Count);
            for (int k = 0; k < keptMemRanges.Count; k++)
            {
                int kOff = (int)memRva + 4 + (k * 16);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(kOff, 8), keptMemRanges[k].StartAddr);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(kOff + 8, 4), keptMemRanges[k].DataSize);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(kOff + 12, 4), keptMemRanges[k].DataRva);
            }

            int memDirOff = (int)streamDirRva + (memIdx * 12);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(memDirOff + 4, 4), compactedMemSize);
            keptDirs[memIdx] = (StreamMemoryList, compactedMemSize, memRva);
        }

        // 7. Secondary defense-in-depth byte scrub for any short known sensitive tokens anywhere in the remaining buffer
        if (extraSensitiveValues != null)
        {
            _ = DiagnosticRedactor.ScrubBinaryMinidump(buffer, extraSensitiveValues);
        }

        // 8. Verify structural integrity: ThreadListStream.NumberOfThreads and ModuleListStream.NumberOfModules must be unchanged
        uint? postNumberOfThreads = TryReadStreamListCount(span, keptDirs, StreamThreadList);
        uint? postNumberOfModules = TryReadStreamListCount(span, keptDirs, StreamModuleList);
        if (preNumberOfThreads != postNumberOfThreads || preNumberOfModules != postNumberOfModules)
        {
            string integrityError =
                $"Minidump structural integrity assertion failed: NumberOfThreads {preNumberOfThreads?.ToString() ?? "null"}->{postNumberOfThreads?.ToString() ?? "null"}, NumberOfModules {preNumberOfModules?.ToString() ?? "null"}->{postNumberOfModules?.ToString() ?? "null"}.";
            Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] [INTEGRITY_ERROR] {integrityError}");
            return new MinidumpSanitizeResult(
                false,
                "STRUCTURAL_CORRUPTION_ABORT",
                integrityError,
                streamsRemoved,
                modulesSanitized,
                backgroundStacksCleared,
                stackTextBytesWiped);
        }

        // 9. Require structural WinDbg openability: consistent non-empty ThreadListStream (3) and non-empty MemoryListStream (5)
        int finalThreadIdx = keptDirs.FindIndex(d => d.Type == StreamThreadList);
        int finalMemIdx = keptDirs.FindIndex(d => d.Type == StreamMemoryList);
        bool hasValidThreadList =
            finalThreadIdx >= 0 &&
            postNumberOfThreads is > 0 &&
            keptDirs[finalThreadIdx].Size >= 4UL + ((ulong)postNumberOfThreads.Value * 48UL);
        bool hasValidMemoryList =
            finalMemIdx >= 0 &&
            keptDirs[finalMemIdx].Size > 4u;

        if (!hasValidThreadList || !hasValidMemoryList)
        {
            string threadDiag = finalThreadIdx < 0
                ? "missing"
                : $"size={keptDirs[finalThreadIdx].Size}, threads={postNumberOfThreads?.ToString() ?? "0"}";
            string memDiag = finalMemIdx < 0
                ? "missing"
                : $"size={keptDirs[finalMemIdx].Size}";
            string openabilityError =
                $"Minidump lacks required WinDbg triage streams (ThreadListStream(3): {threadDiag}; MemoryListStream(5): {memDiag}).";
            Trace.WriteLine($"[DIAGNOSTICS] [MDMP-SANITIZER] [INVALID_MDMP_STREAMS] {openabilityError}");
            return new MinidumpSanitizeResult(
                false,
                "INVALID_MDMP_STREAMS",
                openabilityError,
                streamsRemoved,
                modulesSanitized,
                backgroundStacksCleared,
                stackTextBytesWiped);
        }

        Trace.WriteLine(
            $"[DIAGNOSTICS] [MDMP-SANITIZER] Sanitized MDMP cleanly: streamsRemoved={streamsRemoved}, modulesSanitized={modulesSanitized}, backgroundStacksCleared={backgroundStacksCleared}, stackTextBytesWiped={stackTextBytesWiped}, threads={postNumberOfThreads?.ToString() ?? "n/a"}, modules={postNumberOfModules?.ToString() ?? "n/a"}.");

        return new MinidumpSanitizeResult(
            true,
            "SANITIZED_OK",
            "Minidump structurally sanitized against Zero-PII allowlist.",
            streamsRemoved,
            modulesSanitized,
            backgroundStacksCleared,
            stackTextBytesWiped);
    }

    private static int WipeStackPrintableTextPreservingPointers(
        Span<byte> stackBytes,
        List<(ulong Start, ulong End)> validCodeRanges,
        ulong stackStart,
        ulong stackEnd)
    {
        if (stackBytes.Length == 0)
        {
            return 0;
        }

        bool[] protectedBytes = new bool[stackBytes.Length];
        for (int off = 0; off <= stackBytes.Length - 8; off += 8)
        {
            ulong qword = BinaryPrimitives.ReadUInt64LittleEndian(stackBytes.Slice(off, 8));
            if (qword == 0)
            {
                continue;
            }

            bool isValidPtr = qword >= stackStart && qword < stackEnd;
            if (!isValidPtr)
            {
                for (int m = 0; m < validCodeRanges.Count; m++)
                {
                    if (qword >= validCodeRanges[m].Start && qword < validCodeRanges[m].End)
                    {
                        isValidPtr = true;
                        break;
                    }
                }
            }

            if (isValidPtr)
            {
                for (int b = 0; b < 8; b++)
                {
                    protectedBytes[off + b] = true;
                }
            }
        }

        int wiped = 0;

        // Pass A: Wipe UTF-16LE printable text runs (>= 4 UTF-16LE code units = 8 bytes)
        int i = 0;
        while (i <= stackBytes.Length - 2)
        {
            if (IsUnprotectedUtf16PrintableChar(stackBytes, protectedBytes, i))
            {
                int runStart = i;
                int charCount = 0;
                while (i <= stackBytes.Length - 2 && IsUnprotectedUtf16PrintableChar(stackBytes, protectedBytes, i))
                {
                    charCount++;
                    i += 2;
                }

                if (charCount >= 4)
                {
                    for (int z = runStart; z < i; z++)
                    {
                        if (stackBytes[z] != 0)
                        {
                            stackBytes[z] = 0;
                            wiped++;
                        }
                    }
                }
            }
            else
            {
                i++;
            }
        }

        // Pass B: Wipe ASCII / UTF-8 printable text runs (>= 5 bytes)
        int a = 0;
        while (a < stackBytes.Length)
        {
            if (IsUnprotectedAsciiOrUtf8PrintableByte(stackBytes[a], protectedBytes[a]))
            {
                int runStart = a;
                int asciiCount = 0;
                while (a < stackBytes.Length && IsUnprotectedAsciiOrUtf8PrintableByte(stackBytes[a], protectedBytes[a]))
                {
                    if (stackBytes[a] is >= 0x20 and <= 0x7E)
                    {
                        asciiCount++;
                    }
                    a++;
                }

                if (asciiCount >= 5)
                {
                    for (int z = runStart; z < a; z++)
                    {
                        if (stackBytes[z] != 0)
                        {
                            stackBytes[z] = 0;
                            wiped++;
                        }
                    }
                }
            }
            else
            {
                a++;
            }
        }

        return wiped;
    }

    private static bool IsUnprotectedUtf16PrintableChar(ReadOnlySpan<byte> bytes, bool[] protectedBytes, int offset)
    {
        if (protectedBytes[offset] || protectedBytes[offset + 1])
        {
            return false;
        }

        byte lo = bytes[offset];
        byte hi = bytes[offset + 1];
        // Basic Latin printable (0x0020..0x007E) or Latin-1 Supplement (0x00A0..0x00FF, e.g. ö, ä, ü)
        return hi == 0x00 && ((lo >= 0x20 && lo <= 0x7E) || lo >= 0xA0);
    }

    private static bool IsUnprotectedAsciiOrUtf8PrintableByte(byte b, bool isProtected)
    {
        if (isProtected)
        {
            return false;
        }

        return (b >= 0x20 && b <= 0x7E) || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' || (b >= 0x80 && b <= 0xF4);
    }

    private static string ReadMinidumpString(ReadOnlySpan<byte> span, uint stringRva)
    {
        uint byteLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)stringRva, 4));
        if (byteLen == 0 || byteLen > 4096 || (ulong)stringRva + 4UL + byteLen > (ulong)span.Length)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(span.Slice((int)stringRva + 4, (int)byteLen)).TrimEnd('\0');
    }

    private static uint? TryReadStreamListCount(
        ReadOnlySpan<byte> span,
        List<(uint Type, uint Size, uint Rva)> keptDirs,
        uint streamType)
    {
        int idx = keptDirs.FindIndex(d => d.Type == streamType);
        if (idx < 0 || keptDirs[idx].Size < 4)
        {
            return null;
        }

        uint rva = keptDirs[idx].Rva;
        if (rva < 32 || (ulong)rva + 4UL > (ulong)span.Length)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)rva, 4));
    }

    private static void OverwriteMinidumpStringInPlace(Span<byte> span, uint stringRva, string replacement)
    {
        if (stringRva < 32 || (ulong)stringRva + 4UL > (ulong)span.Length)
        {
            return;
        }

        uint origByteLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)stringRva, 4));
        if (origByteLen == 0 && replacement.Length == 0)
        {
            return;
        }

        if (origByteLen == 0 || origByteLen > 8192 || (ulong)stringRva + 4UL + origByteLen > (ulong)span.Length)
        {
            return;
        }

        // Strictly bound clearSpan to origByteLen — NEVER clear past stringRva + 4 + origByteLen
        // (Win10/11 CSDVersionRva at 0x11A with origByteLen=0 abuts ThreadListStream.NumberOfThreads at 0x11E).
        int clearSpan = (int)origByteLen;
        span.Slice((int)stringRva + 4, clearSpan).Clear();

        byte[] newBytes = string.IsNullOrEmpty(replacement)
            ? []
            : Encoding.Unicode.GetBytes(replacement);
        if (newBytes.Length > origByteLen)
        {
            // Strictly fit inside existing allocation without shifting any RVA offsets while keeping a valid .dll suffix
            newBytes = Encoding.Unicode.GetBytes("mod3p.dll");
            if (newBytes.Length > origByteLen)
            {
                newBytes = Encoding.Unicode.GetBytes("m.dll");
                if (newBytes.Length > origByteLen)
                {
                    int evenCap = (int)(origByteLen & ~1u);
                    newBytes = newBytes[..evenCap];
                }
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice((int)stringRva, 4), (uint)newBytes.Length);
        if (newBytes.Length > 0)
        {
            newBytes.CopyTo(span.Slice((int)stringRva + 4, newBytes.Length));
        }
    }

    internal static string ExtractCrossPlatformFileName(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        int lastBackslash = rawPath.LastIndexOf('\\');
        int lastSlash = rawPath.LastIndexOf('/');
        int cut = Math.Max(lastBackslash, lastSlash);
        return cut >= 0 && cut + 1 < rawPath.Length ? rawPath[(cut + 1)..] : rawPath;
    }
}
