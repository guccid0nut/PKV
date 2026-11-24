// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ShadowCopyUpdateTests.cs
// System: WILLOWMAKER.Tests - Transactional Shadow-Copy Self-Updater Verification
// Subsystem: Strategic Initiative [SR-06] - Zero-Violation File Replacement
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Update;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class ShadowCopyUpdateTests : IDisposable
{
    private readonly string _testRoot;

    public ShadowCopyUpdateTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "Kongor_ShadowCopyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ShadowCopy_AtomicReplace_WhenFileLocked_SuccessfullyReplacesWithoutSharingViolation()
    {
        // Arrange: Create a target file representing a running executable or loaded DLL
        string targetPath = Path.Combine(_testRoot, "hon_x64.exe");
        string originalContent = "HON_VERSION_1_LEGACY_CONTENT_BYTES_0123456789";
        File.WriteAllText(targetPath, originalContent);

        // Open an active read handle simulating Windows holding the executable in memory (CreateProcess SEC_IMAGE)
        using (FileStream lockedStream = new(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            // Verify that a standard direct overwrite throws a sharing violation on Windows
            if (OperatingSystem.IsWindows())
            {
                Action naiveWrite = () => File.WriteAllText(targetPath, "FAIL");
                naiveWrite.Should().Throw<IOException>();
            }

            // Arrange staged update file
            string stagedPath = Path.Combine(_testRoot, "hon_x64.exe.staged");
            string updatedContent = "HON_VERSION_2_MODERN_SHADOW_COPY_CONTENT_9876543210";
            File.WriteAllText(stagedPath, updatedContent);

            // Act: Execute atomic shadow-copy replacement
            bool replaced = ShadowCopyUpdateService.Instance.AtomicReplaceFile(stagedPath, targetPath);

            // Assert
            replaced.Should().BeTrue();

            // The primary path now immediately presents the NEW version
            string currentOnDisk = File.ReadAllText(targetPath);
            currentOnDisk.Should().Be(updatedContent);

            // The locked stream is still intact and can read the original version without crashing
            byte[] readBack = new byte[originalContent.Length];
            lockedStream.Position = 0;
            int bytesRead = lockedStream.Read(readBack, 0, readBack.Length);
            Encoding.UTF8.GetString(readBack, 0, bytesRead).Should().Be(originalContent);
        }

        // After releasing the lock, any remaining .old files are purged and the directory is clean
        ShadowCopyUpdateService.Instance.PurgePendingOldFiles(_testRoot);
        Directory.EnumerateFiles(_testRoot, "*.old", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task ShadowCopy_TransactionRollback_WhenCommitFails_RestoresAllOriginalFiles()
    {
        // Arrange: Create target directory with initial files
        string targetDir = Path.Combine(_testRoot, "target");
        Directory.CreateDirectory(targetDir);

        string file1Target = Path.Combine(targetDir, "moduleA.dll");
        string file2Target = Path.Combine(targetDir, "moduleB.dll");
        File.WriteAllText(file1Target, "ORIGINAL_MODULE_A_v1");
        File.WriteAllText(file2Target, "ORIGINAL_MODULE_B_v1");

        // Staging directory with 2 files
        string stagingDir = Path.Combine(_testRoot, "staging");
        Directory.CreateDirectory(stagingDir);

        string file1Staged = Path.Combine(stagingDir, "moduleA.dll");
        string file2Staged = Path.Combine(stagingDir, "moduleB.dll");
        File.WriteAllText(file1Staged, "NEW_MODULE_A_v2");
        File.WriteAllText(file2Staged, "NEW_MODULE_B_v2");

        // Use a cancellation token to abort the transaction mid-flight
        using CancellationTokenSource cts = new();
        cts.Cancel(); // Pre-cancelled to trigger transaction abort & rollback

        // Act
        ShadowCopyTransactionResult result = await ShadowCopyUpdateService.Instance.ApplyDirectoryUpdateAsync(
            stagingDir, targetDir, cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rolled back");

        // Rollback invariant: Original files MUST remain unaltered
        File.ReadAllText(file1Target).Should().Be("ORIGINAL_MODULE_A_v1");
        File.ReadAllText(file2Target).Should().Be("ORIGINAL_MODULE_B_v1");
    }

    [Fact]
    public void ShadowCopy_PurgePendingOldFiles_DeletesUnlockedOldFiles_AndPreservesTargetFiles()
    {
        // Arrange
        string file1 = Path.Combine(_testRoot, "engine.dll");
        string old1 = Path.Combine(_testRoot, "engine.dll.old");
        string old2 = Path.Combine(_testRoot, "launcher.exe.20260925.old");
        string subDir = Path.Combine(_testRoot, "sub");
        Directory.CreateDirectory(subDir);
        string old3 = Path.Combine(subDir, "cgame_x64.dll.old");

        File.WriteAllText(file1, "ACTIVE_DLL");
        File.WriteAllText(old1, "OLD_1");
        File.WriteAllText(old2, "OLD_2");
        File.WriteAllText(old3, "OLD_3");

        // Act
        int purged = ShadowCopyUpdateService.Instance.PurgePendingOldFiles(_testRoot);

        // Assert
        purged.Should().Be(3);
        File.Exists(file1).Should().BeTrue();
        File.Exists(old1).Should().BeFalse();
        File.Exists(old2).Should().BeFalse();
        File.Exists(old3).Should().BeFalse();
    }

    [Fact]
    public async Task ShadowCopy_ApplyDirectoryUpdate_ReplacesMultipleFilesAndCleansStaging()
    {
        // Arrange
        string targetDir = Path.Combine(_testRoot, "target_app");
        string stagingDir = Path.Combine(_testRoot, "staging_app");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(stagingDir);

        string f1Target = Path.Combine(targetDir, "app.exe");
        string f2Target = Path.Combine(targetDir, "app.dll");
        File.WriteAllText(f1Target, "EXE_V1");
        File.WriteAllText(f2Target, "DLL_V1");

        string f1Staged = Path.Combine(stagingDir, "app.exe");
        string f2Staged = Path.Combine(stagingDir, "app.dll");
        string f3Staged = Path.Combine(stagingDir, "new_asset.json");
        File.WriteAllText(f1Staged, "EXE_V2");
        File.WriteAllText(f2Staged, "DLL_V2");
        File.WriteAllText(f3Staged, "{\"version\": 2}");

        // Act
        ShadowCopyTransactionResult result = await ShadowCopyUpdateService.Instance.ApplyDirectoryUpdateAsync(
            stagingDir, targetDir);

        // Assert
        result.Success.Should().BeTrue();
        result.FilesReplaced.Should().Be(3);
        File.ReadAllText(f1Target).Should().Be("EXE_V2");
        File.ReadAllText(f2Target).Should().Be("DLL_V2");
        File.ReadAllText(Path.Combine(targetDir, "new_asset.json")).Should().Be("{\"version\": 2}");
        Directory.Exists(stagingDir).Should().BeFalse();
    }

    [Fact]
    public void VersionChecker_CleanupPendingOldFiles_InvokesPurgeGracefully()
    {
        // Arrange
        string dummyOld = Path.Combine(_testRoot, "willowmaker.exe.old");
        File.WriteAllText(dummyOld, "OLD_BINARY");

        // Act
        int cleaned = VersionChecker.CleanupPendingOldFiles(_testRoot);

        // Assert
        cleaned.Should().Be(1);
        File.Exists(dummyOld).Should().BeFalse();
    }
}
