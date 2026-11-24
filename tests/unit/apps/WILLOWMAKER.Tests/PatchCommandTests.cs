// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PatchCommandTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 3 CLI Ergonomics (kongor patch)
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kongor.Cli.Commands;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using PeImportAddressTableService = WILLOWMAKER.Core.Services.PeImportAddressTableService;

namespace WILLOWMAKER.Tests;

public sealed class PatchCommandTests
{
    private static byte[] BuildSyntheticPe64(
        (string DllName, string[] Functions)[] imports,
        bool provideCavitySlack = true)
    {
        byte[] peBytes = new byte[4096];

        // 1. DOS Header
        peBytes[0] = 0x4D; // 'M'
        peBytes[1] = 0x5A; // 'Z'
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
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 2, 2), 2); // 2 sections (.text, .rdata)
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 16, 2), 240); // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(fileHeaderOffset + 18, 2), 0x2022); // Characteristics

        // 4. Optional Header 64-bit (240 bytes at eLfanew + 24 = 0x98)
        int optHeaderOffset = eLfanew + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(optHeaderOffset, 2), 0x020B); // PE32+
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 16, 4), 0x1000); // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 32, 4), 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 36, 4), 0x200); // FileAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 56, 4), 0x4000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 60, 4), 0x400); // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 64, 4), 0); // CheckSum
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 108, 4), 16); // NumberOfRvaAndSizes

        // Import Data Directory at OptionalHeader + 120
        uint importDirRva = 0x2000;
        uint importDirSize = (uint)((imports.Length + 1) * 20);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 120, 4), importDirRva);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 124, 4), importDirSize);

        // 5. Section Headers (at optHeaderOffset + 240 = 0x188)
        int sec0Offset = optHeaderOffset + 240;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(peBytes.AsSpan(sec0Offset, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 8, 4), 0x200); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 12, 4), 0x1000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 16, 4), 0x200); // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 20, 4), 0x400); // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 36, 4), 0x60000020); // Characteristics

        int sec1Offset = sec0Offset + 40;
        Encoding.ASCII.GetBytes(".rdata\0\0").CopyTo(peBytes.AsSpan(sec1Offset, 8));
        uint rdataVirtualSize = provideCavitySlack ? 0x200u : 0x600u;
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 8, 4), rdataVirtualSize); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 12, 4), 0x2000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 16, 4), 0x600); // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 20, 4), 0x600); // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 36, 4), 0x40000040); // Characteristics

        // 6. Populate .rdata contents starting at raw offset 0x600 (RVA 0x2000)
        int descriptorBaseOffset = 0x600;
        int descriptorCount = imports.Length;
        int payloadCursor = descriptorBaseOffset + (descriptorCount + 1) * 20;

        for (int i = 0; i < imports.Length; i++)
        {
            var (dllName, functions) = imports[i];
            int descOffset = descriptorBaseOffset + i * 20;

            int nameOffset = payloadCursor;
            byte[] nameBytes = Encoding.ASCII.GetBytes(dllName);
            nameBytes.CopyTo(peBytes.AsSpan(nameOffset));
            peBytes[nameOffset + nameBytes.Length] = 0;
            payloadCursor += nameBytes.Length + 1;
            uint nameRva = 0x2000 + (uint)(nameOffset - 0x600);

            List<uint> hintNameRvas = [];
            foreach (string fn in functions)
            {
                int hintNameOffset = payloadCursor;
                BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(hintNameOffset, 2), 0);
                byte[] fnBytes = Encoding.ASCII.GetBytes(fn);
                fnBytes.CopyTo(peBytes.AsSpan(hintNameOffset + 2));
                peBytes[hintNameOffset + 2 + fnBytes.Length] = 0;
                payloadCursor += 2 + fnBytes.Length + 1;
                hintNameRvas.Add(0x2000 + (uint)(hintNameOffset - 0x600));
            }

            while (payloadCursor % 8 != 0) payloadCursor++;

            int thunkOffset = payloadCursor;
            for (int f = 0; f < hintNameRvas.Count; f++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + f * 8, 8), hintNameRvas[f]);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + hintNameRvas.Count * 8, 8), 0);
            payloadCursor += (hintNameRvas.Count + 1) * 8;
            uint thunkRva = 0x2000 + (uint)(thunkOffset - 0x600);

            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset, 4), thunkRva);
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 4, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 8, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 12, 4), nameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 16, 4), thunkRva);
        }

        peBytes.AsSpan(descriptorBaseOffset + descriptorCount * 20, 20).Clear();
        uint checksum = Kongor.Core.Services.Native.PeImportAddressTableService.CalculatePeChecksum(peBytes, optHeaderOffset + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 64, 4), checksum);

        return peBytes;
    }

    [Fact]
    public void PatchCommand_Execute_Status_ValidGameDir_ReturnsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_Status_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[] { ("KERNEL32.dll", new[] { "ExitProcess" }) };
            byte[] peBytes = BuildSyntheticPe64(imports);
            File.WriteAllBytes(Path.Combine(tempDir, "k2_x64.dll"), peBytes);

            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "status",
                GameDirectory = tempDir
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_Status_MissingDir_ReturnsOne()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), "NonExistent_" + Guid.NewGuid().ToString("N"));

        var command = new PatchCommand();
        var settings = new PatchCommandSettings
        {
            Action = "status",
            GameDirectory = nonExistent
        };

        int exitCode = command.Execute(null!, settings);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void PatchCommand_Execute_Apply_WithSkipProcessCheck_ReturnsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_Apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[] { ("KERNEL32.dll", new[] { "ExitProcess" }) };
            byte[] peBytes = BuildSyntheticPe64(imports);
            File.WriteAllBytes(Path.Combine(tempDir, "k2_x64.dll"), peBytes);

            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "apply",
                GameDirectory = tempDir,
                SkipProcessCheck = true
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_Rollback_ValidBackups_ReturnsZero()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_Rollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string k2 = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2, new byte[100]);
            File.WriteAllBytes(k2 + ".bak", new byte[100]);

            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "rollback",
                GameDirectory = tempDir,
                Tier = "previous",
                SkipProcessCheck = true
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_Rollback_NoBackups_ReturnsOne()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_NoBak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "rollback",
                GameDirectory = tempDir,
                Tier = "previous",
                SkipProcessCheck = true
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_RedirectIat_MissingDllArgs_ReturnsOne()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_MissingArgs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "redirect-iat",
                GameDirectory = tempDir,
                OldDll = null,
                NewDll = null
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_RedirectIat_ValidArgs_PerformsRedirection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_RedirectValid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("libssl-1_1-x64.dll", new[] { "SSL_new" }),
                ("KERNEL32.dll", new[] { "ExitProcess" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports);
            string k2Path = Path.Combine(tempDir, "k2_x64.dll");
            File.WriteAllBytes(k2Path, peBytes);

            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "redirect-iat",
                GameDirectory = tempDir,
                TargetBinary = "k2_x64.dll",
                OldDll = "libssl-1_1-x64.dll",
                NewDll = "kongor_ssl_x64.dll",
                SkipProcessCheck = true
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(k2Path + ".bak"), "Backup must be created by IAT redirection");

            PeImportReport after = PeImportAddressTableService.GetImports(k2Path);
            Assert.True(after.IsValidPe);
            Assert.Contains(after.ImportedModules, m => m.DllName.Equals("kongor_ssl_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(after.ImportedModules, m => m.DllName.Equals("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PatchCommand_Execute_UnknownAction_ReturnsOne()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PatchCmd_Unknown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = new PatchCommand();
            var settings = new PatchCommandSettings
            {
                Action = "some_random_bogus_action",
                GameDirectory = tempDir
            };

            int exitCode = command.Execute(null!, settings);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
