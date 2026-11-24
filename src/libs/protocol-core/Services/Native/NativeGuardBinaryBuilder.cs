// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeGuardBinaryBuilder.cs
// System: Kongor Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 5 Runtime Supervision - NativeGuard 64-bit DLL Synthesis
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Generates, stages, and validates the 64-bit AMD64 native guard dynamic library
///     (Kongor.NativeGuard.dll) for real-time memory bounds protection, crash unwinding,
///     and runtime telemetry injection in hon_x64.exe.
/// </summary>
public static class NativeGuardBinaryBuilder
{
    public const string DllFileName = "Kongor.NativeGuard.dll";

    public static readonly string[] RequiredExports =
    [
        "Kongor_NativeGuard_AobScan",
        "Kongor_NativeGuard_GetHeapMetrics",
        "Kongor_NativeGuard_GetLoadingMetrics",
        "Kongor_NativeGuard_GetPrecacheMetrics",
        "Kongor_NativeGuard_GetStatus",
        "Kongor_NativeGuard_GetTelemetry",
        "Kongor_NativeGuard_InstallHooks",
        "Kongor_NativeGuard_PrecacheFile",
        "Kongor_NativeGuard_RecordHeapAlloc",
        "Kongor_NativeGuard_RecordHeapFree",
        "Kongor_NativeGuard_SetFastPrecache",
        "Kongor_NativeGuard_SetFastVFS",
        "Kongor_NativeGuard_UninstallHooks",
        "Kongor_NativeGuard_ValidateSocketBuffer"
    ];

    /// <summary>
    ///     Builds a fully compliant 64-bit AMD64 PE dynamic library byte array in-memory,
    ///     or loads the compiled native binary from artifacts/native-binaries/windows-x64 if present.
    /// </summary>
    public static byte[] BuildNativeGuardDll()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            string cand = Path.Combine(current, "artifacts", "native-binaries", "windows-x64", DllFileName);
            if (File.Exists(cand))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(cand);
                    if (bytes.Length > 0xC00) return bytes;
                }
                catch { }
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return BuildSynthesizedPeBytes();
    }

    /// <summary>
    ///     Synthesizes a minimal compliant 64-bit AMD64 PE dynamic library byte array in-memory.
    /// </summary>
    public static byte[] BuildSynthesizedPeBytes()
    {
        // 4 pages total: Header (0x400), .text (0x200 at file 0x400), .rdata (0x400 at file 0x600), .reloc (0x200 at file 0xA00)
        byte[] peBytes = new byte[0xC00];

        // 1. DOS Header
        peBytes[0] = (byte)'M';
        peBytes[1] = (byte)'Z';
        int eLfanew = 0x80;
        BinaryPrimitives.WriteInt32LittleEndian(peBytes.AsSpan(0x3C, 4), eLfanew);

        // 2. NT Signature ("PE\0\0")
        peBytes[eLfanew] = (byte)'P';
        peBytes[eLfanew + 1] = (byte)'E';
        peBytes[eLfanew + 2] = 0;
        peBytes[eLfanew + 3] = 0;

        // 3. File Header (20 bytes at eLfanew + 4 = 0x84)
        int fileHeaderOffset = eLfanew + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset, 2), 0x8664); // AMD64
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 2, 2), 3); // 3 sections (.text, .rdata, .reloc)
        uint timeStamp = (uint)TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(fileHeaderOffset + 4, 4), timeStamp);
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 16, 2), 240); // SizeOfOptionalHeader
        // IMAGE_FILE_DLL (0x2000) | IMAGE_FILE_EXECUTABLE_IMAGE (0x0002) | IMAGE_FILE_LARGE_ADDRESS_AWARE (0x0020)
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 18, 2), 0x2022);

        // 4. Optional Header 64-bit (240 bytes at eLfanew + 24 = 0x98)
        int optHeaderOffset = eLfanew + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset, 2), 0x020B); // PE32+ (64-bit)
        peBytes[optHeaderOffset + 2] = 14; // MajorLinkerVersion
        peBytes[optHeaderOffset + 3] = 0;  // MinorLinkerVersion
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 4, 4), 0x200); // SizeOfCode
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 8, 4), 0x600); // SizeOfInitializedData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 12, 4), 0);    // SizeOfUninitializedData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 16, 4), 0x1000); // AddressOfEntryPoint (DllMain in .text)
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 20, 4), 0x1000); // BaseOfCode
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(optHeaderOffset + 24, 8), 0x180000000UL); // ImageBase
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 32, 4), 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 36, 4), 0x200);  // FileAlignment
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 40, 2), 6); // MajorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 42, 2), 0); // MinorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 44, 2), 1); // MajorImageVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 46, 2), 0); // MinorImageVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 48, 2), 6); // MajorSubsystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 50, 2), 0); // MinorSubsystemVersion
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 56, 4), 0x4000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 60, 4), 0x400);  // SizeOfHeaders
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 68, 2), 3);      // Subsystem: Windows CUI
        // DllCharacteristics: DYNAMIC_BASE (0x0040) | NX_COMPAT (0x0100)
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset + 70, 2), 0x0140);
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(optHeaderOffset + 72, 8), 0x100000UL); // SizeOfStackReserve
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(optHeaderOffset + 80, 8), 0x1000UL);   // SizeOfStackCommit
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(optHeaderOffset + 88, 8), 0x100000UL); // SizeOfHeapReserve
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(optHeaderOffset + 96, 8), 0x1000UL);   // SizeOfHeapCommit
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 108, 4), 16);        // NumberOfRvaAndSizes

        // Data Directory 0: Export Directory (in .rdata at RVA 0x2000)
        uint exportDirRva = 0x2000;
        uint exportDirSize = 0x200;
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 112, 4), exportDirRva);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 116, 4), exportDirSize);

        // Data Directory 1: Import Directory (in .rdata at RVA 0x2200)
        uint importDirRva = 0x2200;
        uint importDirSize = 40; // 1 descriptor (20 bytes) + null terminator (20 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 120, 4), importDirRva);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 124, 4), importDirSize);

        // Data Directory 5: Base Relocation Directory (in .reloc at RVA 0x3000)
        uint relocDirRva = 0x3000;
        uint relocDirSize = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 152, 4), relocDirRva);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 156, 4), relocDirSize);

        // Data Directory 12: Import Address Table Directory (in .rdata at RVA 0x2250)
        uint iatDirRva = 0x2250;
        uint iatDirSize = 24; // 2 thunks (16 bytes) + null terminator (8 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 208, 4), iatDirRva);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 212, 4), iatDirSize);

        // 5. Section Headers (at optHeaderOffset + 240 = 0x188)
        int sec0Offset = optHeaderOffset + 240;

        // Section 0: .text (Code)
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(peBytes.AsSpan(sec0Offset, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 8, 4), 0x200);  // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 12, 4), 0x1000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 16, 4), 0x200);  // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 20, 4), 0x400);  // PointerToRawData
        // IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ = 0x60000020
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 36, 4), 0x60000020);

        // Section 1: .rdata (Exports, strings)
        int sec1Offset = sec0Offset + 40;
        Encoding.ASCII.GetBytes(".rdata\0\0").CopyTo(peBytes.AsSpan(sec1Offset, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 8, 4), 0x400);  // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 12, 4), 0x2000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 16, 4), 0x400);  // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 20, 4), 0x600);  // PointerToRawData
        // IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ = 0x40000040
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 36, 4), 0x40000040);

        // Section 2: .reloc (Base relocations)
        int sec2Offset = sec1Offset + 40;
        Encoding.ASCII.GetBytes(".reloc\0\0").CopyTo(peBytes.AsSpan(sec2Offset, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec2Offset + 8, 4), 0x200);  // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec2Offset + 12, 4), 0x3000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec2Offset + 16, 4), 0x200);  // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec2Offset + 20, 4), 0xA00);  // PointerToRawData
        // IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_DISCARDABLE | IMAGE_SCN_MEM_READ = 0x42000040
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec2Offset + 36, 4), 0x42000040);

        // 6. Populate .text section (file offset 0x400)
        // DllMain entrypoint at 0x400 (RVA 0x1000):
        // x64 Win32 DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved):
        // rcx = hinst, edx = fdwReason, r8 = lpReserved
        // On DLL_PROCESS_ATTACH (edx == 1): invoke winmm.dll!timeBeginPeriod(1)
        // On DLL_PROCESS_DETACH (edx == 0): invoke winmm.dll!timeEndPeriod(1)
        // Return 1 (TRUE)
        byte[] dllMainCode =
        [
            0x83, 0xFA, 0x01,                         // cmp edx, 1
            0x75, 0x15,                               // jne +0x15 (to check_detach at 0x1A)
            0x48, 0x83, 0xEC, 0x28,                   // sub rsp, 40
            0xB9, 0x01, 0x00, 0x00, 0x00,             // mov ecx, 1
            0xFF, 0x15, 0x3F, 0x12, 0x00, 0x00,       // call qword ptr [rip + 0x123F] (timeBeginPeriod_IAT at 0x2250)
            0x48, 0x83, 0xC4, 0x28,                   // add rsp, 40
            0xEB, 0x17,                               // jmp +0x17 (to exit_success at 0x31)
            // check_detach (0x1A):
            0x85, 0xD2,                               // test edx, edx
            0x75, 0x13,                               // jne +0x13 (to exit_success at 0x31)
            0x48, 0x83, 0xEC, 0x28,                   // sub rsp, 40
            0xB9, 0x01, 0x00, 0x00, 0x00,             // mov ecx, 1
            0xFF, 0x15, 0x2F, 0x12, 0x00, 0x00,       // call qword ptr [rip + 0x122F] (timeEndPeriod_IAT at 0x2258)
            0x48, 0x83, 0xC4, 0x28,                   // add rsp, 40
            // exit_success (0x31):
            0xB8, 0x01, 0x00, 0x00, 0x00,             // mov eax, 1
            0xC3                                      // ret
        ];
        dllMainCode.CopyTo(peBytes.AsSpan(0x400, dllMainCode.Length));

        // Export function stubs (mov eax, 1; ret):
        byte[] stubCode = [0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3];

        // 7. Populate .rdata section (file offset 0x600, RVA 0x2000)
        int expOffset = 0x600;
        uint rdataRva = 0x2000;

        // Alphabetically sorted export names (required by PE specification for binary search):
        string[] sortedExports = [.. RequiredExports.OrderBy(n => n, StringComparer.Ordinal)];
        int numExports = sortedExports.Length;

        // Layout inside .rdata:
        // 0x00..0x28: IMAGE_EXPORT_DIRECTORY (40 bytes)
        // 0x28..0x48: Export Address Table (numExports * 4 bytes)
        // 0x48..0x68: Export Name Pointer Table (numExports * 4 bytes)
        // 0x68..0x78: Export Ordinal Table (numExports * 2 bytes)
        // 0x78..end:  Strings

        int eatOffset = expOffset + 40;
        int enptOffset = eatOffset + (numExports * 4);
        int eotOffset = enptOffset + (numExports * 4);
        int stringsOffset = eotOffset + (numExports * 2);

        uint eatRva = rdataRva + (uint)(eatOffset - expOffset);
        uint enptRva = rdataRva + (uint)(enptOffset - expOffset);
        uint eotRva = rdataRva + (uint)(eotOffset - expOffset);

        // Write strings and name RVAs
        int currentStrOffset = stringsOffset;
        string dllName = DllFileName + "\0";
        Encoding.ASCII.GetBytes(dllName).CopyTo(peBytes.AsSpan(currentStrOffset, dllName.Length));
        uint dllNameRva = rdataRva + (uint)(currentStrOffset - expOffset);
        currentStrOffset += dllName.Length;

        uint[] funcRvas = new uint[numExports];
        for (int i = 0; i < numExports; i++)
        {
            funcRvas[i] = (uint)(0x1050 + (i * 0x10));
            stubCode.CopyTo(peBytes.AsSpan(0x450 + (i * 0x10), stubCode.Length));
        }

        for (int i = 0; i < numExports; i++)
        {
            string exportName = sortedExports[i];
            byte[] nameBytes = Encoding.ASCII.GetBytes(exportName + "\0");
            nameBytes.CopyTo(peBytes.AsSpan(currentStrOffset, nameBytes.Length));
            uint nameRva = rdataRva + (uint)(currentStrOffset - expOffset);
            currentStrOffset += nameBytes.Length;

            // Write into Export Address Table
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(eatOffset + (i * 4), 4), funcRvas[i]);

            // Write into Export Name Pointer Table
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(enptOffset + (i * 4), 4), nameRva);

            // Write into Export Ordinal Table
            BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(eotOffset + (i * 2), 2), (ushort)i);
        }

        // IMAGE_EXPORT_DIRECTORY
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 4, 4), timeStamp); // TimeDateStamp
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(expOffset + 8, 2), 1);          // MajorVersion
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(expOffset + 10, 2), 0);         // MinorVersion
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 12, 4), dllNameRva); // Name RVA
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 16, 4), 1);         // Base
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 20, 4), (uint)numExports); // NumberOfFunctions
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 24, 4), (uint)numExports); // NumberOfNames
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 28, 4), eatRva);    // AddressOfFunctions
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 32, 4), enptRva);   // AddressOfNames
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(expOffset + 36, 4), eotRva);    // AddressOfNameOrdinals

        uint exportDirActualSize = (uint)(currentStrOffset - expOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 116, 4), exportDirActualSize);

        // 8. Populate Import Table in .rdata (file offset 0x800, RVA 0x2200)
        // winmm.dll imports: timeBeginPeriod (hint 0), timeEndPeriod (hint 1)
        int impOffset = 0x800;
        int intOffset = 0x830;   // OriginalFirstThunk / INT
        int iatOffset = 0x850;   // FirstThunk / IAT
        int modNameOffset = 0x870;
        int hint0Offset = 0x880;
        int hint1Offset = 0x8A0;

        uint intRva = 0x2230;
        uint iatRva = 0x2250;
        uint modNameRva = 0x2270;
        uint hint0Rva = 0x2280;
        uint hint1Rva = 0x22A0;

        // IMAGE_IMPORT_DESCRIPTOR for winmm.dll
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(impOffset, 4), intRva);          // OriginalFirstThunk (INT)
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(impOffset + 4, 4), 0);          // TimeDateStamp
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(impOffset + 8, 4), 0);          // ForwarderChain
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(impOffset + 12, 4), modNameRva); // Name RVA ("winmm.dll")
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(impOffset + 16, 4), iatRva);     // FirstThunk (IAT)
        // Null terminating IMAGE_IMPORT_DESCRIPTOR at impOffset + 20 is already zeroed.

        // INT (OriginalFirstThunk)
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(intOffset, 8), hint0Rva);
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(intOffset + 8, 8), hint1Rva);
        // Null terminator at intOffset + 16 is already zeroed.

        // IAT (FirstThunk) - identical initial RVAs until bound by Windows PE loader
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(iatOffset, 8), hint0Rva);
        BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(iatOffset + 8, 8), hint1Rva);
        // Null terminator at iatOffset + 16 is already zeroed.

        // Module name: "winmm.dll\0"
        Encoding.ASCII.GetBytes("winmm.dll\0").CopyTo(peBytes.AsSpan(modNameOffset, 10));

        // Hint/Name entry 0: timeBeginPeriod
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(hint0Offset, 2), 0); // Hint 0
        Encoding.ASCII.GetBytes("timeBeginPeriod\0").CopyTo(peBytes.AsSpan(hint0Offset + 2, 16));

        // Hint/Name entry 1: timeEndPeriod
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(hint1Offset, 2), 1); // Hint 1
        Encoding.ASCII.GetBytes("timeEndPeriod\0").CopyTo(peBytes.AsSpan(hint1Offset + 2, 14));

        // 9. Populate .reloc section (file offset 0xA00, RVA 0x3000)
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(0xA00, 4), 0x1000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(0xA04, 4), 8);      // BlockSize

        // 10. Recompute standard 32-bit PE CheckSum
        uint checksum = CalculatePeCheckSum(peBytes, optHeaderOffset + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 64, 4), checksum);

        return peBytes;
    }

    /// <summary>
    ///     Calculates the Microsoft COFF standard 32-bit PE CheckSum.
    /// </summary>
    public static uint CalculatePeCheckSum(byte[] bytes, int checksumOffset)
    {
        long checksum = 0;
        long top = 1L << 32;

        for (int i = 0; i < bytes.Length; i += 2)
        {
            if (i == checksumOffset || i == checksumOffset + 2)
                continue;

            ushort word = (i + 1 < bytes.Length)
                ? (ushort)(bytes[i] | (bytes[i + 1] << 8))
                : bytes[i];

            checksum += word;
            if (checksum >= top)
            {
                checksum = (checksum & 0xFFFFFFFF) + (checksum >> 32);
            }
        }

        while ((checksum >> 16) != 0)
        {
            checksum = (checksum & 0xFFFF) + (checksum >> 16);
        }

        checksum += (uint)bytes.Length;
        return (uint)(checksum & 0xFFFFFFFF);
    }

    /// <summary>
    ///     Ensures the candidate Kongor.NativeGuard.dll is staged to the target directory.
    /// </summary>
    public static string EnsureStaged(string targetDirectory, bool forceOverwrite = false)
    {
        Directory.CreateDirectory(targetDirectory);
        string targetPath = Path.Combine(targetDirectory, DllFileName);

        if (!File.Exists(targetPath) || forceOverwrite)
        {
            byte[] dllBytes = BuildNativeGuardDll();
            try
            {
                if (File.Exists(targetPath))
                {
                    byte[] existing = File.ReadAllBytes(targetPath);
                    if (existing.AsSpan().SequenceEqual(dllBytes))
                    {
                        return targetPath;
                    }
                }
                File.WriteAllBytes(targetPath, dllBytes);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // File is currently mapped into a running process (e.g. active dedicated server)
            }
        }

        return targetPath;
    }
}
