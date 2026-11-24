// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GameClientPatcherTests.cs
// System: COMPEL Unit Tests - Client Patcher Service Tests
// ============================================================================

using System.IO;
using System.Security.Cryptography;
using ASPIRE.Common.Patching;
using COMPEL.Services.Patching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class GameClientPatcherTests
{
    [Fact]
    public async Task AuditClientDirectoryAsync_MixedFiles_AccuratelyClassifiesStatus()
    {
        // Arrange
        BinaryDeltaPatchEngine deltaEngine = new();
        GameClientPatcherService patcher = new(deltaEngine, NullLogger<GameClientPatcherService>.Instance);

        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_audit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Up to date file: hon_x64.exe
            byte[] honBytes = "Current hon_x64.exe binary v4.10.2"u8.ToArray();
            string honSha = Convert.ToHexStringLower(SHA256.HashData(honBytes));
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "hon_x64.exe"), honBytes);

            // 2. Outdated file: game_x64.dll (local is v4.10.1, target is v4.10.2)
            byte[] oldGameBytes = "Old game_x64.dll binary v4.10.1"u8.ToArray();
            byte[] newGameBytes = "New game_x64.dll binary v4.10.2"u8.ToArray();
            string newGameSha = Convert.ToHexStringLower(SHA256.HashData(newGameBytes));
            await File.WriteAllBytesAsync(Path.Combine(tempDir, "game_x64.dll"), oldGameBytes);

            // 3. Missing file: k2_x64.dll (not written to disk)
            string k2Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

            PatchManifest manifest = new(
                GameVersion: "4.10.2.0",
                Variant: "client-win64",
                Channel: "release",
                Timestamp: 1726700000,
                Files:
                [
                    new PatchFileEntry("hon_x64.exe", honBytes.Length, honSha),
                    new PatchFileEntry("game_x64.dll", newGameBytes.Length, newGameSha),
                    new PatchFileEntry("k2_x64.dll", 1024, k2Sha)
                ],
                Deltas: []);

            // Act
            ClientPatchAuditReport report = await patcher.AuditClientDirectoryAsync(tempDir, manifest);

            // Assert
            Assert.False(report.IsFullyPatched);
            Assert.Equal(3, report.TotalFiles);
            Assert.Equal(1, report.UpToDateFiles);
            Assert.Equal(1, report.OutdatedFiles);
            Assert.Equal(1, report.MissingFiles);

            OutdatedFileAction outdatedAction = report.Actions.First(a => a.RelativePath == "game_x64.dll");
            Assert.Equal(FilePatchStatus.Outdated, outdatedAction.Status);
            Assert.Equal(newGameSha, outdatedAction.TargetSha256);

            OutdatedFileAction missingAction = report.Actions.First(a => a.RelativePath == "k2_x64.dll");
            Assert.Equal(FilePatchStatus.Missing, missingAction.Status);
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
    public async Task ApplyPatchToFileAsync_OutdatedFile_UpdatesInPlaceWith100PercentHashParity()
    {
        // Arrange
        BinaryDeltaPatchEngine deltaEngine = new();
        GameClientPatcherService patcher = new(deltaEngine, NullLogger<GameClientPatcherService>.Instance);

        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_apply_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] oldCode = "game_x64.dll base engine section: balance v4.10.1 (Pyromancer damage: 350)"u8.ToArray();
            byte[] newCode = "game_x64.dll base engine section: balance v4.10.2 (Pyromancer damage: 320)"u8.ToArray();
            string newCodeSha = Convert.ToHexStringLower(SHA256.HashData(newCode));

            string gameDllPath = Path.Combine(tempDir, "game_x64.dll");
            await File.WriteAllBytesAsync(gameDllPath, oldCode);

            byte[] deltaBytes = deltaEngine.CreateDelta(oldCode, newCode);
            using MemoryStream deltaStream = new(deltaBytes);

            OutdatedFileAction action = new(
                RelativePath: "game_x64.dll",
                Status: FilePatchStatus.Outdated,
                CurrentSha256: Convert.ToHexStringLower(SHA256.HashData(oldCode)),
                TargetSha256: newCodeSha,
                TargetSizeBytes: newCode.Length,
                DeltaUrl: "https://patch.kongor.net/patch/delta");

            // Act
            await patcher.ApplyPatchToFileAsync(tempDir, action, deltaStream);

            // Assert
            byte[] patchedBytes = await File.ReadAllBytesAsync(gameDllPath);
            Assert.Equal(newCode, patchedBytes);

            PatchManifest updatedManifest = new(
                GameVersion: "4.10.2.0",
                Variant: "client-win64",
                Channel: "release",
                Timestamp: 1726700000,
                Files: [new PatchFileEntry("game_x64.dll", newCode.Length, newCodeSha)],
                Deltas: []);

            ClientPatchAuditReport postPatchAudit = await patcher.AuditClientDirectoryAsync(tempDir, updatedManifest);
            Assert.True(postPatchAudit.IsFullyPatched);
            Assert.Equal(1, postPatchAudit.UpToDateFiles);
            Assert.Equal(0, postPatchAudit.OutdatedFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
