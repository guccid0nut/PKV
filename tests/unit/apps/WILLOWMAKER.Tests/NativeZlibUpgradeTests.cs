// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeZlibUpgradeTests.cs
// System: WILLOWMAKER Unit Tests - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 1 Drop-In ABI Replacement of zlibwapi_x64.dll
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kongor.Core.Services.Native;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class NativeZlibUpgradeTests
{
    [Fact]
    public void ExtractPeExportNames_WithRealStagedBinaries_ExtractsAllExpectedExports()
    {
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        if (candidatePath is null) return;

        IReadOnlyList<string> exports = NativeBinaryManager.ExtractPeExportNames(candidatePath);

        Assert.NotNull(exports);
        Assert.True(exports.Count >= 129, $"Expected at least 129 exports, found {exports.Count}");

        // Core zlib APIs
        Assert.Contains("deflate", exports);
        Assert.Contains("inflate", exports);
        Assert.Contains("crc32", exports);
        Assert.Contains("gzread", exports);
        Assert.Contains("gzwrite", exports);

        // Gilles Vollant minizip APIs used by K2 engine VFS (.s2z archives)
        Assert.Contains("unzOpen", exports);
        Assert.Contains("unzClose", exports);
        Assert.Contains("unzReadCurrentFile", exports);
        Assert.Contains("unzOpenCurrentFile", exports);
        Assert.Contains("fill_win32_filefunc64W", exports);

        // Modern 1.3.1 APIs added
        Assert.Contains("crc32_combine_gen", exports);
    }

    [Fact]
    public void VerifyExports_RealCandidateAgainstClientBaseline_ConfirmsZeroMissingSymbols()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string baselinePath = File.Exists(Path.Combine(repoRoot, "games", "client", "zlibwapi_x64.dll.bak.old"))
            ? Path.Combine(repoRoot, "games", "client", "zlibwapi_x64.dll.bak.old")
            : Path.Combine(repoRoot, "games", "client", "zlibwapi_x64.dll");
        string? candidatePath = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        if (!File.Exists(baselinePath) || candidatePath is null) return;

        ExportValidationResult result = NativeBinaryManager.VerifyExports(baselinePath, candidatePath);

        Assert.True(result.IsValid, "Candidate DLL must provide 100% export symbol parity!");
        Assert.Empty(result.MissingSymbols);
        Assert.True(result.BaselineSymbolCount is 129 or 132, $"Baseline symbols should be 129 or 132, was {result.BaselineSymbolCount}");
        Assert.Equal(132, result.CandidateSymbolCount);
        Assert.True(result.AddedSymbols.Count is 0 or 3, $"Added symbols should be 0 or 3, was {result.AddedSymbols.Count}");
    }

    [Fact]
    public void ExtractPeExportNames_CorruptedBuffer_ReturnsEmptyWithoutThrowing()
    {
        // 1. Buffer too small
        byte[] tiny = new byte[10];
        IReadOnlyList<string> resTiny = NativeBinaryManager.ExtractPeExportNames(tiny);
        Assert.Empty(resTiny);

        // 2. Buffer with invalid e_lfanew
        byte[] badLfanew = new byte[100];
        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(badLfanew, 0x3C);
        IReadOnlyList<string> resBad = NativeBinaryManager.ExtractPeExportNames(badLfanew);
        Assert.Empty(resBad);

        // 3. Buffer with zeros
        byte[] zeros = new byte[1024];
        IReadOnlyList<string> resZeros = NativeBinaryManager.ExtractPeExportNames(zeros);
        Assert.Empty(resZeros);
    }

    [Fact]
    public void FindZlibTargets_InMockEnvironment_DiscoversAllFourTargetLocations()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_Targets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "games", "client", "windows-x64"));
        Directory.CreateDirectory(Path.Combine(tempDir, "games", "server", "windows-x64"));

        try
        {
            IReadOnlyList<string> targets = NativeBinaryManager.FindZlibTargets(tempDir);
            Assert.Equal(4, targets.Count);

            Assert.Contains(targets, t => t.EndsWith(Path.Combine("games", "client", "zlibwapi_x64.dll")));
            Assert.Contains(targets, t => t.EndsWith(Path.Combine("games", "client", "windows-x64", "zlibwapi_x64.dll")));
            Assert.Contains(targets, t => t.EndsWith(Path.Combine("games", "server", "zlibwapi_x64.dll")));
            Assert.Contains(targets, t => t.EndsWith(Path.Combine("games", "server", "windows-x64", "zlibwapi_x64.dll")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TwoTierBackupRotation_Lifecycle_RotatesBakToBakOldAndPreservesBaseline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_TwoTierBackup_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client");
        Directory.CreateDirectory(clientDir);

        string repoRoot = NexusTestEnvironment.RepoRoot;
        string realCandidate = Path.Combine(repoRoot, "src", "tools", "native-binaries", "windows-x64", "zlibwapi_x64.dll");

        try
        {
            string targetDll = Path.Combine(clientDir, "zlibwapi_x64.dll");
            string bakPath = targetDll + ".bak";
            string bakOldPath = targetDll + ".bak.old";

            // Stage initial baseline DLL
            byte[] baselineBytes = Encoding.UTF8.GetBytes("ORIGINAL_ZLIB_1_2_11_BASELINE");
            File.WriteAllBytes(targetDll, baselineBytes);

            // Create a fake candidate file (or use real candidate if exists)
            string candidate1 = Path.Combine(tempDir, "candidate_v1.dll");
            if (File.Exists(realCandidate))
            {
                File.Copy(realCandidate, candidate1);
            }
            else
            {
                File.WriteAllBytes(candidate1, Encoding.UTF8.GetBytes("UPGRADE_CANDIDATE_V1"));
            }

            // Step 1: First Upgrade (using synthetic bytes for file rotation lifecycle verification)
            UpgradeResult firstUpgrade = NativeBinaryManager.UpgradeZlib(tempDir, candidate1, skipProcessCheck: true, skipExportCheck: true);
            Assert.True(firstUpgrade.Success);

            // Verification after 1st upgrade:
            // - Active file contains candidate content
            // - .bak contains original baseline content
            // - .bak.old should NOT exist yet (only 1 previous version existed)
            Assert.True(File.Exists(targetDll));
            Assert.True(File.Exists(bakPath));
            Assert.False(File.Exists(bakOldPath));
            Assert.Equal(baselineBytes, File.ReadAllBytes(bakPath));

            // Step 2: Second Upgrade with a newer hotfix binary
            string candidate2 = Path.Combine(tempDir, "candidate_v2.dll");
            byte[] candidate2Bytes = Encoding.UTF8.GetBytes("UPGRADE_CANDIDATE_V2_HOTFIX");
            File.WriteAllBytes(candidate2, candidate2Bytes);

            UpgradeResult secondUpgrade = NativeBinaryManager.UpgradeZlib(tempDir, candidate2, skipProcessCheck: true, skipExportCheck: true);
            Assert.True(secondUpgrade.Success);

            // Verification after 2nd upgrade:
            // - Active file now contains candidate 2 content
            // - .bak now contains candidate 1 content (immediate rollback point)
            // - .bak.old now exists and contains original baseline content (historical recovery point)
            Assert.True(File.Exists(targetDll));
            Assert.True(File.Exists(bakPath));
            Assert.True(File.Exists(bakOldPath));

            Assert.Equal(candidate2Bytes, File.ReadAllBytes(targetDll));
            Assert.Equal(baselineBytes, File.ReadAllBytes(bakOldPath)); // Baseline preserved!

            // Step 3: Rollback to "previous" (default tier: restores candidate 1 from .bak)
            RollbackResult rollPrevious = NativeBinaryManager.RollbackZlib(tempDir, targetTier: "previous", skipProcessCheck: true);
            Assert.True(rollPrevious.Success);
            Assert.Equal(File.ReadAllBytes(candidate1), File.ReadAllBytes(targetDll));

            // Step 4: Rollback to "old" / "baseline" tier (restores original 1.2.11 baseline from .bak.old)
            RollbackResult rollOld = NativeBinaryManager.RollbackZlib(tempDir, targetTier: "old", skipProcessCheck: true);
            Assert.True(rollOld.Success);
            Assert.Equal(baselineBytes, File.ReadAllBytes(targetDll)); // Original baseline successfully restored!
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void UpgradeZlib_WhenCandidateMissingExports_ThrowsInvalidOperationException()
    {
        string? realBaseline = NexusTestEnvironment.TryFindNativeBinary("zlibwapi_x64.dll");
        if (realBaseline is null) return;

        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_ExportGate_" + Guid.NewGuid().ToString("N"));
        string clientDir = Path.Combine(tempDir, "games", "client");
        Directory.CreateDirectory(clientDir);

        try
        {
            // Seed baseline with authentic game client DLL
            string targetDll = Path.Combine(clientDir, "zlibwapi_x64.dll");
            File.Copy(realBaseline, targetDll);

            // Create invalid candidate without required export symbols
            string badCandidate = Path.Combine(tempDir, "bad_candidate.dll");
            File.WriteAllBytes(badCandidate, Encoding.UTF8.GetBytes("INVALID_BINARY_NO_EXPORTS"));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                NativeBinaryManager.UpgradeZlib(tempDir, badCandidate, skipProcessCheck: true, skipExportCheck: false));

            Assert.Contains("export parity check", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void RollbackZlib_WhenNoBackupFilesExist_ReturnsFailureGracefully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_NoBackup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "games", "client"));

        try
        {
            RollbackResult result = NativeBinaryManager.RollbackZlib(tempDir, targetTier: "previous", skipProcessCheck: true);
            Assert.False(result.Success);
            Assert.Contains("No backup files found", result.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void UpgradeZlib_MissingCandidate_ThrowsFileNotFoundException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "Kongor_MissingCand_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Assert.Throws<FileNotFoundException>(() =>
                NativeBinaryManager.UpgradeZlib(tempDir, Path.Combine(tempDir, "non_existent.dll"), skipProcessCheck: true));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void NativeZlibUpgrade_ValidateNativeDependencies_ExecutesAndEmitsLogMessages()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        string repoRoot = NexusTestEnvironment.RepoRoot;
        var logMessages = new List<string>();

        EnginePatchService.ValidateNativeDependencies(repoRoot, msg => logMessages.Add(msg));

        // Should log at least the existing game client/server zlib binaries
        Assert.NotEmpty(logMessages);
        Assert.Contains(logMessages, m => m.Contains("[NATIVE_SECURITY]") && m.Contains("zlibwapi_x64.dll"));
        Assert.Contains(logMessages, m => m.Contains("[NATIVE_SECURITY]") && m.Contains("zlibwapi_x64.dll"));

        // Also test passing direct game directory (games/client)
        string clientDir = Path.Combine(repoRoot, "games", "client");
        if (Directory.Exists(clientDir))
        {
            var clientLogs = new List<string>();
            EnginePatchService.ValidateNativeDependencies(clientDir, msg => clientLogs.Add(msg));
            Assert.NotEmpty(clientLogs);
            Assert.Contains(clientLogs, m => m.Contains("[NATIVE_SECURITY]") && m.Contains("zlibwapi_x64.dll"));
        }
    }

    [Fact]
    public void NativeZlibUpgrade_IsGameOrServerRunning_CanExecuteWithoutExceptions()
    {
        bool isRunning = NativeBinaryManager.IsGameOrServerRunning(out string runningProcs);
        // Process check should evaluate safely without throwing
        if (isRunning)
        {
            Assert.NotEmpty(runningProcs);
        }
        else
        {
            Assert.Empty(runningProcs);
        }
    }
}
