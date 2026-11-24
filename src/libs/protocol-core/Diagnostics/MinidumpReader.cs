// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MinidumpReader.cs
// System: Project Kongor - Protocol Core / Native Forensics
// Subsystem: Pure C# Offline Win32 MDMP Minidump Inspector (Zero External Dependencies)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;

namespace Kongor.Core.Diagnostics;

public readonly record struct MinidumpDirectoryEntry(uint StreamType, uint DataSize, uint Rva);

/// <summary>
/// Factual triage result extracted directly from a binary MDMP stream.
/// Exposes strictly verified offsets, registers, and module bindings with zero speculative text.
/// </summary>
public sealed record MinidumpTriageResult(
    bool IsValidDump,
    string Status,
    string Architecture,
    string ExceptionCodeHex,
    string ExceptionName,
    ulong FaultingAddress,
    string FaultingModule,
    string FaultingRva,
    string TargetFormatted,
    string FaultType,
    string? AccessViolationKind,
    string? AccessViolationTargetHex,
    string? CppExceptionTypeName,
    string? CppExceptionWhatMessage,
    IReadOnlyDictionary<string, string> Registers,
    IReadOnlyList<string> LoadedModules,
    uint StreamCount,
    uint HeaderVersion = 0U,
    IReadOnlyList<MinidumpDirectoryEntry>? StreamDirectories = null);

/// <summary>
/// Pure-C#, zero-reflection, Native AOT-safe reader for Win32 MDMP minidumps.
/// Inspects Stream 7 (SystemInfo), Stream 4 (ModuleList), and Stream 6 (Exception & AMD64 CONTEXT GPRs).
/// </summary>
public static class MinidumpReader
{
    public const uint MdmpSignature = 0x504D444D; // 'MDMP'

    public static MinidumpTriageResult Inspect(string dumpFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFilePath);
        if (!File.Exists(dumpFilePath))
        {
            return CreateInvalidResult("FILE_NOT_FOUND", $"Minidump file does not exist: {dumpFilePath}");
        }

        try
        {
            using FileStream fs = new(dumpFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Inspect(fs);
        }
        catch (Exception ex)
        {
            return CreateInvalidResult("READ_ERROR", $"Failed to open or read minidump: {ex.Message}");
        }
    }

    public static MinidumpTriageResult Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 32)
        {
            return CreateInvalidResult("INVALID_HEADER", "Stream is too short or non-seekable to contain an MDMP header.");
        }

        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        long originalPos = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);

        uint signature = reader.ReadUInt32();
        if (signature != MdmpSignature)
        {
            stream.Seek(originalPos, SeekOrigin.Begin);
            return CreateInvalidResult("INVALID_SIGNATURE", $"Header signature 0x{signature:X8} does not match MDMP (0x504D444D).");
        }

        uint version = reader.ReadUInt32();
        uint streamCount = reader.ReadUInt32();
        uint streamDirRva = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // CheckSum
        _ = reader.ReadUInt32(); // TimeDateStamp
        _ = reader.ReadUInt64(); // Flags

        List<MinidumpDirectoryEntry> directories = new((int)Math.Min(streamCount, 128));
        if (streamDirRva > 0 && streamDirRva + (streamCount * 12) <= stream.Length)
        {
            stream.Seek(streamDirRva, SeekOrigin.Begin);
            for (int i = 0; i < streamCount; i++)
            {
                uint type = reader.ReadUInt32();
                uint size = reader.ReadUInt32();
                uint rva = reader.ReadUInt32();
                directories.Add(new MinidumpDirectoryEntry(type, size, rva));
            }
        }

        // 1. Architecture (Stream 7: SystemInfoStream)
        string architecture = "x64";
        MinidumpDirectoryEntry sysInfoDir = directories.FirstOrDefault(d => d.StreamType == 7);
        if (sysInfoDir.Rva > 0 && sysInfoDir.Rva + 2 <= stream.Length)
        {
            stream.Seek(sysInfoDir.Rva, SeekOrigin.Begin);
            ushort processorArch = reader.ReadUInt16();
            architecture = processorArch switch
            {
                0 => "x86",
                9 => "x64",
                12 => "arm64",
                _ => "x64"
            };
        }

        // 2. Modules (Stream 4: ModuleListStream)
        List<(string Name, ulong BaseOfImage, uint SizeOfImage)> rawModules = new();
        MinidumpDirectoryEntry moduleListDir = directories.FirstOrDefault(d => d.StreamType == 4);
        if (moduleListDir.Rva > 0 && moduleListDir.Rva + 4 <= stream.Length)
        {
            stream.Seek(moduleListDir.Rva, SeekOrigin.Begin);
            uint numModules = reader.ReadUInt32();
            for (int m = 0; m < numModules && stream.Position + 108 <= stream.Length; m++)
            {
                ulong baseOfImage = reader.ReadUInt64();
                uint sizeOfImage = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // CheckSum
                _ = reader.ReadUInt32(); // TimeDateStamp
                uint moduleNameRva = reader.ReadUInt32();
                stream.Seek(84, SeekOrigin.Current); // Skip VS_FIXEDFILEINFO (52) + CvRecord (16) + MiscRecord (16)

                long returnPos = stream.Position;
                string modName = "unknown.dll";
                if (moduleNameRva > 0 && moduleNameRva + 4 <= stream.Length)
                {
                    stream.Seek(moduleNameRva, SeekOrigin.Begin);
                    uint strByteLen = reader.ReadUInt32();
                    if (strByteLen > 0 && strByteLen < 4096 && stream.Position + strByteLen <= stream.Length)
                    {
                        byte[] nameBytes = reader.ReadBytes((int)strByteLen);
                        string fullPath = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                        modName = Path.GetFileName(fullPath);
                    }
                }
                stream.Seek(returnPos, SeekOrigin.Begin);
                rawModules.Add((modName, baseOfImage, sizeOfImage));
            }
        }

        IReadOnlyList<MemoryRegionDescriptor> moduleRegions = ProcessMemoryMapInspector.ClassifyMemoryRegions(
            rawModules.Select(m => (
                m.BaseOfImage,
                m.BaseOfImage,
                (ulong)m.SizeOfImage,
                ProcessMemoryMapInspector.MEM_COMMIT,
                ProcessMemoryMapInspector.PAGE_EXECUTE_READ,
                ProcessMemoryMapInspector.MEM_IMAGE,
                (string?)m.Name)));

        // 3. Exception & CONTEXT (Stream 6: ExceptionStream)
        MinidumpDirectoryEntry excDir = directories.FirstOrDefault(d => d.StreamType == 6);
        if (excDir.Rva == 0 || excDir.Rva + 12 > stream.Length)
        {
            // Stream 6 absent: Live process freeze snapshot or memory-only dump
            stream.Seek(originalPos, SeekOrigin.Begin);
            return new MinidumpTriageResult(
                IsValidDump: true,
                Status: "LIVE_SNAPSHOT_NO_EXCEPTION",
                Architecture: architecture,
                ExceptionCodeHex: "0x00000000",
                ExceptionName: "NO_EXCEPTION_RECORD (LIVE_PROCESS_SNAPSHOT)",
                FaultingAddress: 0UL,
                FaultingModule: rawModules.Count > 0 ? rawModules[0].Name : "hon_x64.exe",
                FaultingRva: "0x0",
                TargetFormatted: rawModules.Count > 0 ? $"{rawModules[0].Name}+0x0" : "hon_x64.exe+0x0",
                FaultType: "Live process snapshot or hang capture (no fatal exception was recorded by the engine)",
                AccessViolationKind: null,
                AccessViolationTargetHex: null,
                CppExceptionTypeName: null,
                CppExceptionWhatMessage: null,
                Registers: new Dictionary<string, string>(),
                LoadedModules: rawModules.Select(m => m.Name).ToList(),
                StreamCount: (uint)directories.Count,
                HeaderVersion: version,
                StreamDirectories: directories);
        }

        stream.Seek(excDir.Rva, SeekOrigin.Begin);
        uint threadId = reader.ReadUInt32();
        uint alignment = reader.ReadUInt32();
        uint excCode = reader.ReadUInt32();
        ulong excAddress = 0UL;
        ulong[] excParams = [];

        if (excDir.DataSize >= 32 && excDir.Rva + 32 <= stream.Length)
        {
            uint excFlags = reader.ReadUInt32();
            ulong excRecord = reader.ReadUInt64();
            excAddress = reader.ReadUInt64();
            uint numParams = reader.ReadUInt32();
            uint unusedAlignment = reader.ReadUInt32();

            int paramCount = Math.Clamp((int)numParams, 0, 15);
            excParams = new ulong[paramCount];
            for (int p = 0; p < paramCount && stream.Position + 8 <= stream.Length; p++)
            {
                excParams[p] = reader.ReadUInt64();
            }
        }

        // ThreadContext descriptor is located at offset 160 of MINIDUMP_EXCEPTION_STREAM (after 152B MINIDUMP_EXCEPTION)
        Dictionary<string, string> registers = new(StringComparer.OrdinalIgnoreCase);
        if (excDir.Rva + 168 <= stream.Length)
        {
            stream.Seek(excDir.Rva + 160, SeekOrigin.Begin);
            uint contextDataSize = reader.ReadUInt32();
            uint contextRva = reader.ReadUInt32();

            if (contextRva > 0 && contextRva + contextDataSize <= stream.Length && architecture == "x64" && contextDataSize >= 0x100)
            {
                // CONTEXT_AMD64 GPRs
                stream.Seek(contextRva + 0x78, SeekOrigin.Begin);
                ulong rax = reader.ReadUInt64();
                ulong rcx = reader.ReadUInt64();
                ulong rdx = reader.ReadUInt64();
                ulong rbx = reader.ReadUInt64();
                ulong rsp = reader.ReadUInt64();
                ulong rbp = reader.ReadUInt64();
                ulong rsi = reader.ReadUInt64();
                ulong rdi = reader.ReadUInt64();
                ulong r8  = reader.ReadUInt64();
                ulong r9  = reader.ReadUInt64();
                ulong r10 = reader.ReadUInt64();
                ulong r11 = reader.ReadUInt64();
                ulong r12 = reader.ReadUInt64();
                ulong r13 = reader.ReadUInt64();
                ulong r14 = reader.ReadUInt64();
                ulong r15 = reader.ReadUInt64();
                ulong rip = reader.ReadUInt64();

                registers["RAX"] = $"0x{rax:X16}";
                registers["RCX"] = $"0x{rcx:X16}";
                registers["RDX"] = $"0x{rdx:X16}";
                registers["RBX"] = $"0x{rbx:X16}";
                registers["RSP"] = $"0x{rsp:X16}";
                registers["RBP"] = $"0x{rbp:X16}";
                registers["RSI"] = $"0x{rsi:X16}";
                registers["RDI"] = $"0x{rdi:X16}";
                registers["R8"]  = $"0x{r8:X16}";
                registers["R9"]  = $"0x{r9:X16}";
                registers["R10"] = $"0x{r10:X16}";
                registers["R11"] = $"0x{r11:X16}";
                registers["R12"] = $"0x{r12:X16}";
                registers["R13"] = $"0x{r13:X16}";
                registers["R14"] = $"0x{r14:X16}";
                registers["R15"] = $"0x{r15:X16}";
                registers["RIP"] = $"0x{rip:X16}";

                if (excAddress == 0UL && rip != 0UL)
                {
                    excAddress = rip;
                }
            }
        }

        if (!registers.ContainsKey("RIP") && excAddress != 0UL)
        {
            registers["RIP"] = $"0x{excAddress:X16}";
        }

        // Map Module + RVA
        MemoryRegionDescriptor? matched = ProcessMemoryMapInspector.LookupRegion(moduleRegions, excAddress);
        string faultingModule;
        string faultingRva;
        string targetFormatted;

        if (matched != null)
        {
            faultingModule = matched.AssociatedModuleOrLabel.ToLowerInvariant();
            ulong rvaVal = excAddress - matched.BaseAddress;
            faultingRva = $"0x{rvaVal:X}";
            targetFormatted = $"{faultingModule}+{faultingRva}";
        }
        else
        {
            faultingModule = "unknown_module.dll";
            faultingRva = $"0x{excAddress:X}";
            targetFormatted = $"{faultingModule}+{faultingRva}";
        }

        string excCodeHex = $"0x{excCode:X8}";
        string excName = excCode switch
        {
            0xC0000005U => "STATUS_ACCESS_VIOLATION",
            0xC0000006U => "STATUS_IN_PAGE_ERROR",
            0x80000003U => "STATUS_BREAKPOINT",
            0xC00000FDU => "STATUS_STACK_OVERFLOW",
            0xC000001DU => "STATUS_ILLEGAL_INSTRUCTION",
            0xC0000094U => "STATUS_INTEGER_DIVIDE_BY_ZERO",
            0xC0000008U => "STATUS_INVALID_HANDLE",
            0xC0000374U => "STATUS_HEAP_CORRUPTION",
            0xC0000409U => "STATUS_STACK_BUFFER_OVERRUN",
            0xE06D7363U => "MSVC_CPP_EXCEPTION",
            0x40010006U => "DBG_PRINTEXCEPTION_C",
            0x406D1388U => "MS_VC_EXCEPTION",
            _ => $"EXCEPTION_{excCode:X8}"
        };

        string faultType = excName;
        string? avKind = null;
        string? avTargetHex = null;
        string? cppTypeName = null;
        string? cppWhatMessage = null;

        if ((excCode == 0xC0000005U || excCode == 0xC0000006U) && excParams.Length >= 2)
        {
            (string Kind, string TargetAddressHex, string Description) avInfo =
                CrashTriageOrchestrator.ClassifyAccessViolationFault(excParams[0], excParams[1]);
            avKind = avInfo.Kind;
            avTargetHex = avInfo.TargetAddressHex;
            faultType = avInfo.Description;
        }
        else if (excCode == 0xE06D7363U && excParams.Length >= 3)
        {
            MsvcCppExceptionMetadata cppMeta = CrashTriageOrchestrator.DecodeMsvcCppExceptionParameters(excParams);
            cppTypeName = cppMeta.DemangledTypeName;
            cppWhatMessage = cppMeta.WhatMessage;
            faultType = $"[GEODE-RTTI] Thrown C++ Type: {cppMeta.DemangledTypeName} (ThrowInfo=0x{cppMeta.ThrowInfoAddress:X16})";
        }

        stream.Seek(originalPos, SeekOrigin.Begin);
        return new MinidumpTriageResult(
            IsValidDump: true,
            Status: "CRASH_TRIAGED",
            Architecture: architecture,
            ExceptionCodeHex: excCodeHex,
            ExceptionName: excName,
            FaultingAddress: excAddress,
            FaultingModule: faultingModule,
            FaultingRva: faultingRva,
            TargetFormatted: targetFormatted,
            FaultType: faultType,
            AccessViolationKind: avKind,
            AccessViolationTargetHex: avTargetHex,
            CppExceptionTypeName: cppTypeName,
            CppExceptionWhatMessage: cppWhatMessage,
            Registers: registers,
            LoadedModules: rawModules.Select(m => m.Name).ToList(),
            StreamCount: (uint)directories.Count,
            HeaderVersion: version,
            StreamDirectories: directories);
    }

    private static MinidumpTriageResult CreateInvalidResult(string status, string faultType) => new(
        IsValidDump: false,
        Status: status,
        Architecture: "unknown",
        ExceptionCodeHex: "0x00000000",
        ExceptionName: "INVALID_MINIDUMP",
        FaultingAddress: 0UL,
        FaultingModule: "unknown.dll",
        FaultingRva: "0x0",
        TargetFormatted: "unknown.dll+0x0",
        FaultType: faultType,
        AccessViolationKind: null,
        AccessViolationTargetHex: null,
        CppExceptionTypeName: null,
        CppExceptionWhatMessage: null,
        Registers: new Dictionary<string, string>(),
        LoadedModules: Array.Empty<string>(),
        StreamCount: 0U);
}
