// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ShadowCopyUpdateService.cs
// System: NEXUS CDN Broker - Transactional Shadow-Copy Self-Updater
// Subsystem: Strategic Initiative [SR-06] - Zero-Violation File Replacement
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WILLOWMAKER.Core.Services.Update;

/// <summary>
///     Encapsulates the result of a multi-file shadow-copy update transaction.
/// </summary>
public sealed record ShadowCopyTransactionResult(
    bool Success,
    int FilesReplaced,
    int FilesShadowCopied,
    IReadOnlyList<string> ReplacedFiles,
    IReadOnlyList<string> ShadowCopiedFiles,
    string? ErrorMessage = null)
{
    public static ShadowCopyTransactionResult Successful(
        IReadOnlyList<string> replaced,
        IReadOnlyList<string> shadowCopied) =>
        new(true, replaced.Count, shadowCopied.Count, replaced, shadowCopied);

    public static ShadowCopyTransactionResult Failed(
        string errorMessage,
        IReadOnlyList<string>? replaced = null,
        IReadOnlyList<string>? shadowCopied = null) =>
        new(false, replaced?.Count ?? 0, shadowCopied?.Count ?? 0, replaced ?? [], shadowCopied ?? [], errorMessage);
}

/// <summary>
///     Provides contract for atomic shadow-copy file replacements and transactional directory updates.
/// </summary>
public interface IShadowCopyUpdateService
{
    /// <summary>
    ///     Atomically replaces the destination file with the staged source file. If the destination file
    ///     is in-use or locked (e.g. running executable or loaded DLL), renames it to a .old shadow copy
    ///     before placing the new version.
    /// </summary>
    bool AtomicReplaceFile(string sourceStagedPath, string targetDestinationPath);

    /// <summary>
    ///     Applies a full directory update transactionally. If any file fails to commit,
    ///     automatically rolls back all shadow-copied files to their original state.
    /// </summary>
    Task<ShadowCopyTransactionResult> ApplyDirectoryUpdateAsync(
        string stagingDirectory,
        string targetDirectory,
        CancellationToken ct = default);

    /// <summary>
    ///     Scans the target directory and recursively deletes any leftover *.old shadow files.
    ///     If a file is currently locked, registers it for deletion upon the next system reboot.
    /// </summary>
    int PurgePendingOldFiles(string targetDirectory);
}

/// <summary>
///     Default implementation of <see cref="IShadowCopyUpdateService"/>.
/// </summary>
public sealed class ShadowCopyUpdateService : IShadowCopyUpdateService
{
    public static ShadowCopyUpdateService Instance { get; } = new();

    /// <inheritdoc />
    public bool AtomicReplaceFile(string sourceStagedPath, string targetDestinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDestinationPath);

        if (!File.Exists(sourceStagedPath))
            throw new FileNotFoundException($"Source staged file not found: {sourceStagedPath}", sourceStagedPath);

        string? targetDir = Path.GetDirectoryName(targetDestinationPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // If target file doesn't exist yet, simply move staged file into place
        if (!File.Exists(targetDestinationPath))
        {
            NativeFileOperations.MoveFileAtomic(sourceStagedPath, targetDestinationPath, overwrite: false);
            return true;
        }

        // Target exists. First try standard atomic overwrite
        try
        {
            NativeFileOperations.MoveFileAtomic(sourceStagedPath, targetDestinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (NativeFileOperations.IsSharingViolation(ex))
        {
            // Destination file is actively in-use (running EXE or loaded DLL).
            // Execute shadow-copy rename: target -> target.old
            string oldPath = GenerateUniqueOldPath(targetDestinationPath);

            // Rename the locked target out of the way
            NativeFileOperations.MoveFileAtomic(targetDestinationPath, oldPath, overwrite: true);

            // Move the staged update into the primary target path
            try
            {
                NativeFileOperations.MoveFileAtomic(sourceStagedPath, targetDestinationPath, overwrite: true);
            }
            catch
            {
                // Rollback: restore the original file if staging move failed
                try
                {
                    NativeFileOperations.MoveFileAtomic(oldPath, targetDestinationPath, overwrite: true);
                }
                catch { }

                throw;
            }

            // Register the .old file for deletion upon next system reboot
            NativeFileOperations.ScheduleDeleteOnReboot(oldPath);

            // Attempt immediate best-effort deletion (may fail if still mapped)
            try
            {
                File.Delete(oldPath);
            }
            catch { }

            return true;
        }
    }

    /// <inheritdoc />
    public async Task<ShadowCopyTransactionResult> ApplyDirectoryUpdateAsync(
        string stagingDirectory,
        string targetDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!Directory.Exists(stagingDirectory))
            return ShadowCopyTransactionResult.Failed($"Staging directory does not exist: {stagingDirectory}");

        Directory.CreateDirectory(targetDirectory);

        List<string> stagedFiles = Directory
            .EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .ToList();

        if (stagedFiles.Count == 0)
            return ShadowCopyTransactionResult.Successful([], []);

        List<string> replacedFiles = [];
        List<string> shadowCopiedFiles = [];
        List<(string Target, string? OldShadow, bool ExistedBefore)> transactionLog = [];

        await Task.Yield();

        try
        {
            foreach (string stagedFile in stagedFiles)
            {
                ct.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(stagingDirectory, stagedFile);
                string targetFile = Path.Combine(targetDirectory, relativePath);
                string? targetDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                bool exists = File.Exists(targetFile);

                if (!exists)
                {
                    NativeFileOperations.MoveFileAtomic(stagedFile, targetFile, overwrite: false);
                    transactionLog.Add((targetFile, null, false));
                    replacedFiles.Add(relativePath);
                    continue;
                }

                // File exists. Attempt atomic replace or shadow-copy
                try
                {
                    NativeFileOperations.MoveFileAtomic(stagedFile, targetFile, overwrite: true);
                    transactionLog.Add((targetFile, null, true));
                    replacedFiles.Add(relativePath);
                }
                catch (Exception ex) when (NativeFileOperations.IsSharingViolation(ex))
                {
                    string oldShadow = GenerateUniqueOldPath(targetFile);
                    NativeFileOperations.MoveFileAtomic(targetFile, oldShadow, overwrite: true);

                    try
                    {
                        NativeFileOperations.MoveFileAtomic(stagedFile, targetFile, overwrite: true);
                    }
                    catch
                    {
                        // Restore immediate shadow
                        NativeFileOperations.MoveFileAtomic(oldShadow, targetFile, overwrite: true);
                        throw;
                    }

                    NativeFileOperations.ScheduleDeleteOnReboot(oldShadow);
                    transactionLog.Add((targetFile, oldShadow, true));
                    shadowCopiedFiles.Add(relativePath);
                    replacedFiles.Add(relativePath);
                }
            }

            // Cleanup empty staging directory if possible
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch { }

            return ShadowCopyTransactionResult.Successful(replacedFiles, shadowCopiedFiles);
        }
        catch (Exception ex)
        {
            // Transaction Rollback Phase
            RollbackTransaction(transactionLog);
            return ShadowCopyTransactionResult.Failed($"Update transaction rolled back: {ex.Message}", replacedFiles, shadowCopiedFiles);
        }
    }

    /// <inheritdoc />
    public int PurgePendingOldFiles(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            return 0;

        int deletedCount = 0;
        try
        {
            var oldFiles = Directory.EnumerateFiles(targetDirectory, "*.old", SearchOption.AllDirectories);
            foreach (string file in oldFiles)
            {
                try
                {
                    // Clear read-only attribute if set
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                    }

                    File.Delete(file);
                    deletedCount++;
                }
                catch
                {
                    // Still locked: re-register for next reboot
                    NativeFileOperations.ScheduleDeleteOnReboot(file);
                }
            }
        }
        catch { }

        return deletedCount;
    }

    private static string GenerateUniqueOldPath(string targetPath)
    {
        string candidate = targetPath + ".old";
        if (!File.Exists(candidate))
            return candidate;

        string dir = Path.GetDirectoryName(targetPath) ?? string.Empty;
        string name = Path.GetFileName(targetPath);
        string stamp = TimeProvider.System.GetUtcNow().ToString("yyyyMMddHHmmssfff");
        return Path.Combine(dir, $"{name}.{stamp}.old");
    }

    private static void RollbackTransaction(List<(string Target, string? OldShadow, bool ExistedBefore)> log)
    {
        // Revert in reverse order of modifications
        for (int i = log.Count - 1; i >= 0; i--)
        {
            var entry = log[i];
            try
            {
                if (!entry.ExistedBefore)
                {
                    // File did not exist before; remove the newly moved file
                    if (File.Exists(entry.Target))
                    {
                        File.Delete(entry.Target);
                    }
                }
                else if (entry.OldShadow != null && File.Exists(entry.OldShadow))
                {
                    // Was shadow-copied; restore old version
                    NativeFileOperations.MoveFileAtomic(entry.OldShadow, entry.Target, overwrite: true);
                }
            }
            catch { }
        }
    }
}
