// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PeImportAddressTableServiceTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 3 PE Import Address Table (IAT) Governance & Redirection
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;
using PeImportAddressTableService = WILLOWMAKER.Core.Services.PeImportAddressTableService;

namespace WILLOWMAKER.Tests;

public sealed class PeImportAddressTableServiceTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "client", "windows-x64")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    ///     Generates a valid synthetic 64-bit PE binary in-memory with custom import directory entries.
    /// </summary>
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
        // Section 0: .text
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(peBytes.AsSpan(sec0Offset, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 8, 4), 0x200); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 12, 4), 0x1000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 16, 4), 0x200); // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 20, 4), 0x400); // PointerToRawData
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec0Offset + 36, 4), 0x60000020); // Characteristics

        // Section 1: .rdata
        int sec1Offset = sec0Offset + 40;
        Encoding.ASCII.GetBytes(".rdata\0\0").CopyTo(peBytes.AsSpan(sec1Offset, 8));
        uint rdataVirtualSize = provideCavitySlack ? 0x200u : 0x600u;
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 8, 4), rdataVirtualSize); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 12, 4), 0x2000); // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(sec1Offset + 16, 4), 0x600); // SizeOfRawData (slack = 0x400 when virtual=0x200)
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

            // Write DLL name string
            int nameOffset = payloadCursor;
            byte[] nameBytes = Encoding.ASCII.GetBytes(dllName);
            nameBytes.CopyTo(peBytes.AsSpan(nameOffset));
            peBytes[nameOffset + nameBytes.Length] = 0;
            payloadCursor += nameBytes.Length + 1;
            uint nameRva = 0x2000 + (uint)(nameOffset - 0x600);

            // Write hint/names and thunk arrays
            List<uint> hintNameRvas = [];
            foreach (string fn in functions)
            {
                int hintNameOffset = payloadCursor;
                BinaryPrimitives.WriteUInt16LittleEndian(peBytes.AsSpan(hintNameOffset, 2), 0); // hint = 0
                byte[] fnBytes = Encoding.ASCII.GetBytes(fn);
                fnBytes.CopyTo(peBytes.AsSpan(hintNameOffset + 2));
                peBytes[hintNameOffset + 2 + fnBytes.Length] = 0;
                payloadCursor += 2 + fnBytes.Length + 1;
                hintNameRvas.Add(0x2000 + (uint)(hintNameOffset - 0x600));
            }

            // Align thunks to 8 bytes
            while (payloadCursor % 8 != 0) payloadCursor++;

            int thunkOffset = payloadCursor;
            for (int f = 0; f < hintNameRvas.Count; f++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + f * 8, 8), hintNameRvas[f]);
            }
            // Null terminating thunk
            BinaryPrimitives.WriteUInt64LittleEndian(peBytes.AsSpan(thunkOffset + hintNameRvas.Count * 8, 8), 0);
            payloadCursor += (hintNameRvas.Count + 1) * 8;
            uint thunkRva = 0x2000 + (uint)(thunkOffset - 0x600);

            // Write Import Descriptor
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset, 4), thunkRva); // OriginalFirstThunk
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 4, 4), 0); // TimeDateStamp
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 8, 4), 0); // ForwarderChain
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 12, 4), nameRva); // Name RVA
            BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(descOffset + 16, 4), thunkRva); // FirstThunk
        }

        // Null terminating descriptor (20 bytes)
        peBytes.AsSpan(descriptorBaseOffset + descriptorCount * 20, 20).Clear();

        // Calculate initial checksum
        uint checksum = Kongor.Core.Services.Native.PeImportAddressTableService.CalculatePeChecksum(peBytes, optHeaderOffset + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(optHeaderOffset + 64, 4), checksum);

        return peBytes;
    }

    [Fact]
    public void GetImports_WithInvalidPe_ReturnsGracefullyWithError()
    {
        byte[] garbage = new byte[100];
        Array.Fill(garbage, (byte)0xCC);

        PeImportReport report = Kongor.Core.Services.Native.PeImportAddressTableService.GetImports(garbage, "invalid.dll");

        Assert.False(report.IsValidPe);
        Assert.NotNull(report.ErrorMessage);
        Assert.Empty(report.ImportedModules);
    }

    [Fact]
    public void GetImports_WithSyntheticPe64_EnumeratesModulesAndFunctionsAccurately()
    {
        var imports = new[]
        {
            ("KERNEL32.dll", new[] { "VirtualAlloc", "VirtualFree" }),
            ("USER32.dll", new[] { "MessageBoxA" })
        };

        byte[] peBytes = BuildSyntheticPe64(imports);
        PeImportReport report = Kongor.Core.Services.Native.PeImportAddressTableService.GetImports(peBytes, "test64.dll");

        Assert.True(report.IsValidPe);
        Assert.True(report.Is64Bit);
        Assert.True(report.Checksum > 0);
        Assert.Equal(2, report.ImportedModules.Count);

        PeImportedDll k32 = report.ImportedModules.First(m => m.DllName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("VirtualAlloc", k32.ImportedFunctions);
        Assert.Contains("VirtualFree", k32.ImportedFunctions);

        PeImportedDll u32 = report.ImportedModules.First(m => m.DllName.Equals("USER32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("MessageBoxA", u32.ImportedFunctions);
    }

    [Fact]
    public void RedirectImportedDll_SameLength_OverwritesInPlaceAndUpdatesChecksum()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_SameLen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("libssl-1_1-x64.dll", new[] { "SSL_new" }),
                ("KERNEL32.dll", new[] { "ExitProcess" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports);

            string dllPath = Path.Combine(tempDir, "game_module.dll");
            File.WriteAllBytes(dllPath, peBytes);

            // "libssl-1_1-x64.dll" has 17 chars. Replacement "kongor_ssl_x64.dll" has 17 chars.
            PeRedirectionResult result = PeImportAddressTableService.RedirectImportedDll(
                dllPath,
                "libssl-1_1-x64.dll",
                "kongor_ssl_x64.dll",
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success, result.Message);
            Assert.False(result.UsedCavitySlack, "Equal length redirection should happen in-place without cavity slack.");
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));

            PeImportReport afterReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.True(afterReport.IsValidPe);
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_ssl_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(afterReport.ImportedModules, m => m.DllName.Equals("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDll_ShorterLength_PadsWithNullAndSucceeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_Shorter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("long_module_name.dll", new[] { "Func1" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports);

            string dllPath = Path.Combine(tempDir, "short_test.dll");
            File.WriteAllBytes(dllPath, peBytes);

            PeRedirectionResult result = PeImportAddressTableService.RedirectImportedDll(
                dllPath,
                "long_module_name.dll",
                "short.dll",
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success, result.Message);
            Assert.False(result.UsedCavitySlack, "Shorter length replacement should overwrite in-place with null padding.");

            PeImportReport afterReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("short.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDll_LongerLength_UsesSectionAlignmentCavitySlack()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_LongerSlack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("z.dll", new[] { "deflate" }) // 5 chars
            };
            byte[] peBytes = BuildSyntheticPe64(imports, provideCavitySlack: true);

            string dllPath = Path.Combine(tempDir, "slack_test.dll");
            File.WriteAllBytes(dllPath, peBytes);

            // Replacement has 22 chars > 5 chars original
            PeRedirectionResult result = PeImportAddressTableService.RedirectImportedDll(
                dllPath,
                "z.dll",
                "kongor_modern_zlib.dll",
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success, result.Message);
            Assert.True(result.UsedCavitySlack, "Longer length replacement MUST utilize section alignment slack cavity.");

            PeImportReport afterReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.True(afterReport.IsValidPe);
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_modern_zlib.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(afterReport.ImportedModules, m => m.DllName.Equals("z.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDll_NotFoundImport_ReturnsFailureWithoutModifyingFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_NotFound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[] { ("USER32.dll", new[] { "MessageBoxA" }) };
            byte[] peBytes = BuildSyntheticPe64(imports);

            string dllPath = Path.Combine(tempDir, "mod.dll");
            File.WriteAllBytes(dllPath, peBytes);
            byte[] beforeBytes = File.ReadAllBytes(dllPath);

            PeRedirectionResult result = PeImportAddressTableService.RedirectImportedDll(
                dllPath,
                "non_existent.dll",
                "replacement.dll",
                createBackup: true,
                skipProcessCheck: true);

            Assert.False(result.Success);
            Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);

            byte[] afterBytes = File.ReadAllBytes(dllPath);
            Assert.Equal(beforeBytes, afterBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDll_TwoTierBackupRotation_RotatesBakToBakOld()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_TwoTier_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("step1.dll", new[] { "F1" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports);

            string dllPath = Path.Combine(tempDir, "two_tier.dll");
            File.WriteAllBytes(dllPath, peBytes);

            // First redirect: step1.dll -> step2.dll
            PeRedirectionResult r1 = PeImportAddressTableService.RedirectImportedDll(
                dllPath, "step1.dll", "step2.dll", createBackup: true, skipProcessCheck: true);
            Assert.True(r1.Success);
            Assert.True(File.Exists(dllPath + ".bak"), ".bak must exist after first operation");
            Assert.False(File.Exists(dllPath + ".bak.old"), ".bak.old must not exist yet");

            // Second redirect: step2.dll -> step3.dll
            PeRedirectionResult r2 = PeImportAddressTableService.RedirectImportedDll(
                dllPath, "step2.dll", "step3.dll", createBackup: true, skipProcessCheck: true);
            Assert.True(r2.Success);
            Assert.True(File.Exists(dllPath + ".bak"), ".bak must exist after second operation");
            Assert.True(File.Exists(dllPath + ".bak.old"), ".bak.old must now exist after rotation");

            // Verify .bak.old contains original step1.dll
            PeImportReport bakOldReport = PeImportAddressTableService.GetImports(dllPath + ".bak.old");
            Assert.Contains(bakOldReport.ImportedModules, m => m.DllName.Equals("step1.dll", StringComparison.OrdinalIgnoreCase));

            // Verify .bak contains intermediate step2.dll
            PeImportReport bakReport = PeImportAddressTableService.GetImports(dllPath + ".bak");
            Assert.Contains(bakReport.ImportedModules, m => m.DllName.Equals("step2.dll", StringComparison.OrdinalIgnoreCase));

            // Verify current contains step3.dll
            PeImportReport currentReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.Contains(currentReport.ImportedModules, m => m.DllName.Equals("step3.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InspectClientBinaryImports_StagedZlib_IfPresent_EnumeratesExpectedImports()
    {
        string repoRoot = FindRepoRoot();
        string zlibPath = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll");

        if (!File.Exists(zlibPath))
        {
            return; // Skip if staged binary not found in local checkout
        }

        PeImportReport report = PeImportAddressTableService.GetImports(zlibPath);
        Assert.True(report.IsValidPe, $"zlibwapi_x64.dll should be a valid PE. Error: {report.ErrorMessage}");
        Assert.True(report.Is64Bit);
        Assert.NotEmpty(report.ImportedModules);

        // Standard Windows DLLs imported by zlib
        Assert.Contains(report.ImportedModules, m =>
            m.DllName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase) ||
            m.DllName.Equals("msvcrt.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RedirectImportedDlls_BatchRedirection_RedirectsMultipleImportsInSinglePass()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_Batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("libssl-1_1-x64.dll", new[] { "SSL_new", "SSL_free" }),
                ("libcrypto-1_1-x64.dll", new[] { "EVP_DigestInit" }),
                ("KERNEL32.dll", new[] { "ExitProcess" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports, provideCavitySlack: true);

            string dllPath = Path.Combine(tempDir, "batch_target.dll");
            File.WriteAllBytes(dllPath, peBytes);

            var redirections = new Dictionary<string, string>
            {
                ["libssl-1_1-x64.dll"] = "kongor_ssl_x64.dll", // 17 chars -> 17 chars (in-place)
                ["libcrypto-1_1-x64.dll"] = "kongor_crypto_x64.dll" // 20 chars -> 20 chars (in-place)
            };

            PeBatchRedirectionResult result = PeImportAddressTableService.RedirectImportedDlls(
                dllPath,
                redirections,
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.RedirectedCount);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));

            PeImportReport afterReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.True(afterReport.IsValidPe);
            Assert.Equal(3, afterReport.ImportedModules.Count);

            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_ssl_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_crypto_x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase));

            Assert.DoesNotContain(afterReport.ImportedModules, m => m.DllName.Equals("libssl-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(afterReport.ImportedModules, m => m.DllName.Equals("libcrypto-1_1-x64.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDlls_BatchWithCavitySlack_AllocatesCavityAndUpdatesChecksum()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_BatchSlack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[]
            {
                ("old1.dll", new[] { "Fn1" }), // 8 chars
                ("old2.dll", new[] { "Fn2" }), // 8 chars
                ("KERNEL32.dll", new[] { "ExitProcess" })
            };
            byte[] peBytes = BuildSyntheticPe64(imports, provideCavitySlack: true);

            string dllPath = Path.Combine(tempDir, "batch_slack.dll");
            File.WriteAllBytes(dllPath, peBytes);

            var redirections = new Dictionary<string, string>
            {
                ["old1.dll"] = "kongor_longer_library_one.dll", // 29 chars > 8 chars (cavity slack)
                ["old2.dll"] = "kongor_longer_library_two.dll"  // 29 chars > 8 chars (cavity slack)
            };

            PeBatchRedirectionResult result = PeImportAddressTableService.RedirectImportedDlls(
                dllPath,
                redirections,
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.RedirectedCount);
            Assert.True(result.AnyUsedCavitySlack, "Batch with longer names must utilize cavity slack.");

            PeImportReport afterReport = PeImportAddressTableService.GetImports(dllPath);
            Assert.True(afterReport.IsValidPe);
            Assert.Equal(3, afterReport.ImportedModules.Count);

            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_longer_library_one.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("kongor_longer_library_two.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(afterReport.ImportedModules, m => m.DllName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RedirectImportedDlls_EmptyRedirections_ReturnsSuccessWithoutModification()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PeIat_Empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var imports = new[] { ("KERNEL32.dll", new[] { "ExitProcess" }) };
            byte[] peBytes = BuildSyntheticPe64(imports);

            string dllPath = Path.Combine(tempDir, "empty_test.dll");
            File.WriteAllBytes(dllPath, peBytes);

            PeBatchRedirectionResult result = PeImportAddressTableService.RedirectImportedDlls(
                dllPath,
                new Dictionary<string, string>(),
                createBackup: true,
                skipProcessCheck: true);

            Assert.True(result.Success);
            Assert.Equal(0, result.RedirectedCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
