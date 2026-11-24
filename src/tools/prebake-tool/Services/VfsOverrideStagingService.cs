// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using Kongor.Modding.Vfs;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Prebaking.Generators;

public static class VfsOverrideStagingService
{
    public static async Task<StagingResult> StageLooseFilesAsync(
        string gameClientDir,
        IReadOnlyDictionary<string, byte[]> filesToStage,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameClientDir);
        ArgumentNullException.ThrowIfNull(filesToStage);

        string overrideDir = Path.Combine(gameClientDir, "override");
        if (!Directory.Exists(overrideDir)) Directory.CreateDirectory(overrideDir);

        string backupDir = Path.Combine(gameClientDir, ".mod_backups",
            "prebaked_" + TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds());
        List<string> stagedPaths = new(filesToStage.Count);
        List<BackupFileInfo> overwritten = [];

        foreach (KeyValuePair<string, byte[]> kvp in filesToStage)
        {
            ct.ThrowIfCancellationRequested();
            string relPath = kvp.Key.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string targetPath = Path.Combine(overrideDir, relPath);

            string? parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);

            if (File.Exists(targetPath))
            {
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

                string backupFile = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".bak");
                byte[] oldBytes = await File.ReadAllBytesAsync(targetPath, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(backupFile, oldBytes, ct).ConfigureAwait(false);
                string sha256 = Convert.ToHexString(SHA256.HashData(oldBytes));

                overwritten.Add(new BackupFileInfo(Path.Combine("override", relPath).Replace('\\', '/'), backupFile,
                    sha256));
            }

            await File.WriteAllBytesAsync(targetPath, kvp.Value, ct).ConfigureAwait(false);
            stagedPaths.Add(Path.Combine("override", relPath).Replace('\\', '/'));
        }

        // Write hot reload trigger
        string triggerPath = Path.Combine(overrideDir, ".hotreload.trigger");
        string triggerContent = $"PREBAKE_RELOAD_{TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()}";
        await File.WriteAllTextAsync(triggerPath, triggerContent, Encoding.UTF8, ct).ConfigureAwait(false);
        stagedPaths.Add("override/.hotreload.trigger");

        // Write rollback journal entry
        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Vfs,
            TargetDirectory = gameClientDir,
            AppliedMods = [new AppliedModInfo("PrebakedEntityBundle", "1.0.0", "PREBAKED_M1")],
            CreatedFiles = stagedPaths,
            OverwrittenFiles = overwritten
        };

        await RollbackJournal.SaveJournalAsync(gameClientDir, journal, ct).ConfigureAwait(false);
        logger?.LogInformation("Staged {Count} files to VFS override in {OverrideDir}", stagedPaths.Count, overrideDir);

        return new StagingResult(true, stagedPaths.Count, stagedPaths);
    }
}

public readonly record struct StagingResult(bool Success, int FilesStaged, IReadOnlyList<string> StagedPaths);
