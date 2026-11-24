// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using Kongor.Modding.Core.Models;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Vfs;

/// <summary>
///     Non-destructive VFS injector writing loose patched files directly to games/client/override/.
///     K2 engine natively prioritizes loose files in override/ over all .s2z archives.
/// </summary>
public static class LayeredVfsInjector
{
    public static async Task<JournalEntry> InjectAsync(
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> preparedFiles,
        IReadOnlyList<ModPackage> appliedMods,
        CancellationToken ct = default)
    {
        string overrideDir = Path.Combine(gameClientDirectory, "override");
        string gameDir = Path.Combine(gameClientDirectory, "game");
        string backupDir = Path.Combine(gameClientDirectory, ".mod_backups");
        if (!Directory.Exists(overrideDir)) Directory.CreateDirectory(overrideDir);

        List<string> createdFiles = [];
        List<BackupFileInfo> overwrittenFiles = [];
        List<(string TargetPath, byte[] Content)> stagedWrites = [];

        // Phase 1 (WAL Pre-Flight): Validate all paths & stage backups + journal BEFORE mutating any target file!
        foreach (KeyValuePair<string, byte[]> kvp in preparedFiles)
        {
            string normRelPath = ModPackage.NormalizeZipPath(kvp.Key);
            byte[] content = kvp.Value;

            string targetFilePath = ModPackage.ResolveContainedDiskPath(overrideDir, normRelPath);
            string relToClient = $"override/{normRelPath}";
            if (File.Exists(targetFilePath))
            {
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                string overrideBackupPath =
                    Path.Combine(backupDir, $"{Path.GetFileName(targetFilePath)}.override.{Guid.NewGuid():N}.bak");
                CopyBackupAndFlush(targetFilePath, overrideBackupPath);
                overwrittenFiles.Add(new BackupFileInfo(relToClient, overrideBackupPath, string.Empty));
            }
            else
            {
                createdFiles.Add(relToClient);
            }

            stagedWrites.Add((targetFilePath, content));

            string inGameFilePath = ModPackage.ResolveContainedDiskPath(gameDir, normRelPath);
            if (File.Exists(inGameFilePath))
            {
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                string backupFilePath = Path.Combine(backupDir, $"{Path.GetFileName(inGameFilePath)}.{Guid.NewGuid():N}.bak");
                CopyBackupAndFlush(inGameFilePath, backupFilePath);

                string inGameRelToClient = $"game/{normRelPath}";
                overwrittenFiles.Add(new BackupFileInfo(inGameRelToClient, backupFilePath, string.Empty));
                stagedWrites.Add((inGameFilePath, content));
            }
        }

        string triggerPath = ModPackage.ResolveContainedDiskPath(overrideDir, ".hotreload.trigger");
        if (File.Exists(triggerPath))
        {
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
            string triggerBackupPath = Path.Combine(backupDir, $".hotreload.trigger.{Guid.NewGuid():N}.bak");
            CopyBackupAndFlush(triggerPath, triggerBackupPath);
            overwrittenFiles.Add(new BackupFileInfo("override/.hotreload.trigger", triggerBackupPath, string.Empty));
        }
        else
        {
            createdFiles.Add("override/.hotreload.trigger");
        }

        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Vfs,
            TargetDirectory = gameClientDirectory,
            AppliedMods =
                [.. appliedMods.Select(m => new AppliedModInfo(m.Manifest.Name, m.Manifest.Version, m.Sha256Hash))],
            CreatedFiles = createdFiles,
            OverwrittenFiles = overwrittenFiles
        };

        await RollbackJournal.SaveJournalAsync(gameClientDirectory, journal, ct).ConfigureAwait(false);

        // Phase 2: Apply mutations with automatic WAL rollback on any mid-write failure
        try
        {
            foreach ((string targetPath, byte[] content) in stagedWrites)
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(targetPath, content, ct).ConfigureAwait(false);
            }

            string triggerContent = $"MOD_RELOAD_{TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()}";
            await File.WriteAllTextAsync(triggerPath, triggerContent, Encoding.UTF8, ct).ConfigureAwait(false);
            return journal;
        }
        catch (Exception writeEx)
        {
            Console.Error.WriteLine(
                $"[LayeredVfsInjector][WAL] Mid-write failure on '{gameClientDirectory}': {writeEx.Message}. Executing WAL rollback.");
            await RollbackJournal.ExecuteRollbackAsync(gameClientDirectory, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void CopyBackupAndFlush(string sourceFilePath, string backupFilePath)
    {
        using FileStream src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream dst = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        src.CopyTo(dst);
        dst.Flush(flushToDisk: true);
    }

    public static async Task<JournalEntry> InjectAsync(
        IVirtualFileSystem vfs,
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> preparedFiles,
        IReadOnlyList<ModPackage> appliedMods,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        string overridePrefix = string.IsNullOrEmpty(gameClientDirectory)
            ? "override"
            : $"{gameClientDirectory.TrimEnd('/')}/override";
        string gamePrefix = string.IsNullOrEmpty(gameClientDirectory)
            ? "game"
            : $"{gameClientDirectory.TrimEnd('/')}/game";
        string backupPrefix = string.IsNullOrEmpty(gameClientDirectory)
            ? ".mod_backups"
            : $"{gameClientDirectory.TrimEnd('/')}/.mod_backups";

        if (!vfs.DirectoryExists(overridePrefix)) vfs.CreateDirectory(overridePrefix);

        List<string> createdFiles = [];
        List<BackupFileInfo> overwrittenFiles = [];
        List<(string VirtualTargetPath, byte[] Content)> stagedWrites = [];

        foreach (KeyValuePair<string, byte[]> kvp in preparedFiles)
        {
            string normRelPath = ModPackage.NormalizeZipPath(kvp.Key);
            byte[] content = kvp.Value;

            string targetVirtualPath = $"{overridePrefix}/{normRelPath}";
            string relToClient = $"override/{normRelPath}";

            if (vfs.FileExists(targetVirtualPath))
            {
                if (!vfs.DirectoryExists(backupPrefix)) vfs.CreateDirectory(backupPrefix);
                string overrideBackupPath =
                    $"{backupPrefix}/{Path.GetFileName(targetVirtualPath)}.override.{Guid.NewGuid():N}.bak";
                byte[] origOverrideBytes = await vfs.ReadAllBytesAsync(targetVirtualPath, ct).ConfigureAwait(false);
                await vfs.WriteAllBytesAsync(overrideBackupPath, origOverrideBytes, ct).ConfigureAwait(false);
                overwrittenFiles.Add(new BackupFileInfo(relToClient, overrideBackupPath, string.Empty));
            }
            else
            {
                createdFiles.Add(relToClient);
            }

            stagedWrites.Add((targetVirtualPath, content));

            string inGameVirtualPath = $"{gamePrefix}/{normRelPath}";
            if (vfs.FileExists(inGameVirtualPath))
            {
                if (!vfs.DirectoryExists(backupPrefix)) vfs.CreateDirectory(backupPrefix);
                string backupVirtualPath = $"{backupPrefix}/{Path.GetFileName(inGameVirtualPath)}.{Guid.NewGuid():N}.bak";
                byte[] origBytes = await vfs.ReadAllBytesAsync(inGameVirtualPath, ct).ConfigureAwait(false);
                await vfs.WriteAllBytesAsync(backupVirtualPath, origBytes, ct).ConfigureAwait(false);

                string inGameRel = $"game/{normRelPath}";
                overwrittenFiles.Add(new BackupFileInfo(inGameRel, backupVirtualPath, string.Empty));
                stagedWrites.Add((inGameVirtualPath, content));
            }
        }

        string triggerPath = $"{overridePrefix}/.hotreload.trigger";
        if (!vfs.FileExists(triggerPath))
        {
            createdFiles.Add("override/.hotreload.trigger");
        }

        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Vfs,
            TargetDirectory = gameClientDirectory,
            AppliedMods =
                [.. appliedMods.Select(m => new AppliedModInfo(m.Manifest.Name, m.Manifest.Version, m.Sha256Hash))],
            CreatedFiles = createdFiles,
            OverwrittenFiles = overwrittenFiles
        };

        await RollbackJournal.SaveJournalAsync(vfs, gameClientDirectory, journal, ct).ConfigureAwait(false);

        try
        {
            foreach ((string virtualTargetPath, byte[] content) in stagedWrites)
            {
                await vfs.WriteAllBytesAsync(virtualTargetPath, content, ct).ConfigureAwait(false);
            }

            string triggerContent = $"MOD_RELOAD_{TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()}";
            await vfs.WriteAllTextAsync(triggerPath, triggerContent, ct).ConfigureAwait(false);
            return journal;
        }
        catch (Exception writeEx)
        {
            Console.Error.WriteLine(
                $"[LayeredVfsInjector][WAL] VFS mid-write failure on '{gameClientDirectory}': {writeEx.Message}. Executing WAL rollback.");
            await RollbackJournal.ExecuteRollbackAsync(vfs, gameClientDirectory, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }
}
