// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Kongor.Core.Vfs;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Vfs;

/// <summary>
///     Direct archive injector compiling all patched and new mod assets into game/resources999.s2z.
/// </summary>
public static class DirectArchiveInjector
{
    private static readonly string[] LooseTiersShadowingGameArchives =
    [
        .. ClientModMountSpec.CanonicalMountTiers
            .Where(t => !t.Equals("base", StringComparison.OrdinalIgnoreCase))
    ];

    public static async Task<JournalEntry> InjectAsync(
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> preparedFiles,
        IReadOnlyList<ModPackage> appliedMods,
        string archiveName = "resources999.s2z",
        CancellationToken ct = default)
    {
        string gameDir = Path.Combine(gameClientDirectory, "game");
        if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);

        string safeArchiveName = ModPackage.NormalizeZipPath(archiveName);
        if (safeArchiveName.Contains('/'))
        {
            string archiveAlert =
                $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected nested or traversal archive name '{archiveName}'.";
            Console.Error.WriteLine(archiveAlert);
            throw new System.Security.SecurityException(archiveAlert);
        }

        // Pre-validate all prepared file keys OUTSIDE the IOException fallback catch so SecurityException always fails closed!
        Dictionary<string, byte[]> validatedPreparedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, byte[]> kvp in preparedFiles)
        {
            string normRel = ModPackage.NormalizeZipPath(kvp.Key);
            _ = ModPackage.ResolveContainedDiskPath(gameDir, normRel);
            validatedPreparedFiles[normRel] = kvp.Value;
        }

        string targetArchive = ModPackage.ResolveContainedDiskPath(gameDir, safeArchiveName);
        if (validatedPreparedFiles.Count == 0)
        {
            await RollbackJournal.ExecuteRollbackAsync(gameClientDirectory, ct).ConfigureAwait(false);
            RollbackJournal.CleanResidualModOptions(gameClientDirectory);

            if (File.Exists(targetArchive))
            {
                try
                {
                    File.Delete(targetArchive);
                }
                catch
                {
                    // In use by running game process; non-critical
                }
            }

            JournalEntry emptyJournal = new()
            {
                AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
                Mode = InjectionMode.Archive,
                TargetDirectory = gameClientDirectory,
                AppliedMods = [],
                CreatedFiles = [],
                OverwrittenFiles = []
            };
            await RollbackJournal.SaveJournalAsync(gameClientDirectory, emptyJournal, ct).ConfigureAwait(false);
            return emptyJournal;
        }

        List<string> createdFiles = [];
        List<BackupFileInfo> overwrittenFiles = [];
        bool walCommitted = false;

        try
        {
            // Execute rollback of previous tracked mod injections before applying new set to ensure clean baseline
            if (RollbackJournal.HasActiveModifications(gameClientDirectory))
            {
                bool baselineClean = await RollbackJournal.ExecuteRollbackAsync(gameClientDirectory, ct).ConfigureAwait(false);
                if (!baselineClean)
                {
                    throw new IOException(
                        $"Previous mod rollback on '{gameClientDirectory}' left locked target files pending restore; deferring to LayeredVfsInjector.");
                }
            }

            IReadOnlyDictionary<string, byte[]> effectiveFiles =
                await EnsureModOptionsBundledAsync(null, gameClientDirectory, validatedPreparedFiles, safeArchiveName, ct)
                    .ConfigureAwait(false);

            string backupDir = Path.Combine(gameClientDirectory, ".mod_backups");
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            string archiveRelPath = $"game/{safeArchiveName}";
            if (File.Exists(targetArchive))
            {
                string backupPath = Path.Combine(backupDir, $"{safeArchiveName}.bak.{Guid.NewGuid():N}");
                CopyBackupAndFlush(targetArchive, backupPath);
                overwrittenFiles.Add(new BackupFileInfo(archiveRelPath, backupPath, string.Empty));
            }
            else
            {
                createdFiles.Add(archiveRelPath);
            }

            // Phase 1 (True Write-Ahead Log): Stage ALL loose-file backups and persist WAL journal BEFORE mutating archive or loose files!
            List<(string LooseTargetPath, byte[] Content)> stagedLooseWrites = StageLooseBackupsAndWrites(
                gameClientDirectory,
                backupDir,
                effectiveFiles,
                createdFiles,
                overwrittenFiles);

            JournalEntry journal = new()
            {
                AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
                Mode = InjectionMode.Archive,
                TargetDirectory = gameClientDirectory,
                AppliedMods =
                    [.. appliedMods.Select(m => new AppliedModInfo(m.Manifest.Name, m.Manifest.Version, m.Sha256Hash))],
                CreatedFiles = createdFiles,
                OverwrittenFiles = overwrittenFiles
            };

            await RollbackJournal.SaveJournalAsync(gameClientDirectory, journal, ct).ConfigureAwait(false);
            walCommitted = true;

            // Phase 2: Write target archive and apply staged loose writes
            await S2zArchiveWriter.WriteArchiveAsync(targetArchive, effectiveFiles, CompressionLevel.Optimal, ct)
                .ConfigureAwait(false);

            foreach ((string looseTargetPath, byte[] content) in stagedLooseWrites)
            {
                string? parentDir = Path.GetDirectoryName(looseTargetPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                await File.WriteAllBytesAsync(looseTargetPath, content, ct).ConfigureAwait(false);
            }

            if (!appliedMods.Any(m => m.Manifest.Name.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || m.Manifest.Name.Contains("hbmod", StringComparison.OrdinalIgnoreCase)))
            {
                RollbackJournal.CleanResidualModOptions(gameClientDirectory);
            }

            return journal;
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine(
                $"[DirectArchiveInjector][WAL] IOException during archive injection on '{gameClientDirectory}' ('{ioEx.Message}'). Rolling back partial mutations before VFS fallback.");
            if (walCommitted)
            {
                await RollbackJournal.ExecuteRollbackAsync(gameClientDirectory, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // If target archive or loose tier is locked, fall back seamlessly to layered VFS injection
            return await LayeredVfsInjector.InjectAsync(gameClientDirectory, validatedPreparedFiles, appliedMods, ct)
                .ConfigureAwait(false);
        }
    }

    public static async Task<JournalEntry> InjectAsync(
        IVirtualFileSystem vfs,
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> preparedFiles,
        IReadOnlyList<ModPackage> appliedMods,
        string archiveName = "resources999.s2z",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        string gameDir = string.IsNullOrEmpty(gameClientDirectory)
            ? "game"
            : $"{gameClientDirectory.TrimEnd('/')}/game";

        if (!vfs.DirectoryExists(gameDir)) vfs.CreateDirectory(gameDir);

        string safeArchiveName = ModPackage.NormalizeZipPath(archiveName);
        if (safeArchiveName.Contains('/'))
        {
            string archiveAlert =
                $"[SECURITY-ALERT][MODDING-ZIPSLIP] Rejected nested or traversal archive name '{archiveName}'.";
            Console.Error.WriteLine(archiveAlert);
            throw new System.Security.SecurityException(archiveAlert);
        }

        Dictionary<string, byte[]> validatedPreparedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, byte[]> kvp in preparedFiles)
        {
            validatedPreparedFiles[ModPackage.NormalizeZipPath(kvp.Key)] = kvp.Value;
        }

        string targetArchive = $"{gameDir}/{safeArchiveName}";
        if (validatedPreparedFiles.Count == 0)
        {
            await RollbackJournal.ExecuteRollbackAsync(vfs, gameClientDirectory, ct).ConfigureAwait(false);

            if (vfs.FileExists(targetArchive))
            {
                try
                {
                    vfs.DeleteFile(targetArchive);
                }
                catch
                {
                    // Non-critical
                }
            }

            JournalEntry emptyJournal = new()
            {
                AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
                Mode = InjectionMode.Archive,
                TargetDirectory = gameClientDirectory,
                AppliedMods = [],
                CreatedFiles = [],
                OverwrittenFiles = []
            };
            await RollbackJournal.SaveJournalAsync(vfs, gameClientDirectory, emptyJournal, ct).ConfigureAwait(false);
            return emptyJournal;
        }

        List<string> createdFiles = [];
        List<BackupFileInfo> overwrittenFiles = [];

        await RollbackJournal.ExecuteRollbackAsync(vfs, gameClientDirectory, ct).ConfigureAwait(false);

        if (vfs.FileExists(targetArchive))
        {
            string backupDir = string.IsNullOrEmpty(gameClientDirectory)
                ? ".mod_backups"
                : $"{gameClientDirectory.TrimEnd('/')}/.mod_backups";
            if (!vfs.DirectoryExists(backupDir)) vfs.CreateDirectory(backupDir);

            string backupPath = $"{backupDir}/{safeArchiveName}.bak.{Guid.NewGuid():N}";
            byte[] existingBytes = await vfs.ReadAllBytesAsync(targetArchive, ct).ConfigureAwait(false);
            await vfs.WriteAllBytesAsync(backupPath, existingBytes, ct).ConfigureAwait(false);
            overwrittenFiles.Add(new BackupFileInfo(
                $"game/{safeArchiveName}",
                backupPath,
                string.Empty));
        }
        else
        {
            createdFiles.Add($"game/{safeArchiveName}");
        }

        IReadOnlyDictionary<string, byte[]> effectiveFiles = await EnsureModOptionsBundledAsync(
            vfs, gameClientDirectory, validatedPreparedFiles, safeArchiveName, ct).ConfigureAwait(false);

        await S2zArchiveWriter.WriteArchiveAsync(vfs, targetArchive, effectiveFiles, CompressionLevel.Optimal, ct)
            .ConfigureAwait(false);

        await SyncEffectiveFilesToLooseLocationsAsync(vfs, gameClientDirectory, effectiveFiles, createdFiles, overwrittenFiles, ct).ConfigureAwait(false);

        if (!appliedMods.Any(m => m.Manifest.Name.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || m.Manifest.Name.Contains("hbmod", StringComparison.OrdinalIgnoreCase)))
        {
            RollbackJournal.CleanResidualModOptions(gameClientDirectory);
        }

        JournalEntry journal = new()
        {
            AppliedTimestampUtc = TimeProvider.System.GetUtcNow(),
            Mode = InjectionMode.Archive,
            TargetDirectory = gameClientDirectory,
            AppliedMods =
                [.. appliedMods.Select(m => new AppliedModInfo(m.Manifest.Name, m.Manifest.Version, m.Sha256Hash))],
            CreatedFiles = createdFiles,
            OverwrittenFiles = overwrittenFiles
        };

        await RollbackJournal.SaveJournalAsync(vfs, gameClientDirectory, journal, ct).ConfigureAwait(false);
        return journal;
    }

    private static void CopyBackupAndFlush(string sourceFilePath, string backupFilePath)
    {
        using FileStream src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream dst = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.WriteThrough);
        src.CopyTo(dst);
        dst.Flush(flushToDisk: true);
    }

    private static List<(string LooseTargetPath, byte[] Content)> StageLooseBackupsAndWrites(
        string gameClientDirectory,
        string backupDir,
        IReadOnlyDictionary<string, byte[]> effectiveFiles,
        List<string> createdFiles,
        List<BackupFileInfo> overwrittenFiles)
    {
        string gameDir = Path.Combine(gameClientDirectory, "game");
        string[] searchRoots =
        [
            .. LooseTiersShadowingGameArchives.Select(t =>
                Path.GetFullPath(Path.Combine(gameClientDirectory, t.Replace('/', Path.DirectorySeparatorChar))))
        ];

        HashSet<string> updatedPaths = new(StringComparer.OrdinalIgnoreCase);
        List<(string LooseTargetPath, byte[] Content)> stagedWrites = [];

        foreach (KeyValuePair<string, byte[]> kvp in effectiveFiles)
        {
            string normRel = ModPackage.NormalizeZipPath(kvp.Key);
            byte[] content = kvp.Value;

            bool existedLoose = false;
            foreach (string root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;
                string looseFilePath = ModPackage.ResolveContainedDiskPath(root, normRel);
                if (File.Exists(looseFilePath))
                {
                    existedLoose = true;
                    string fullLoosePath = Path.GetFullPath(looseFilePath);
                    if (!updatedPaths.Add(fullLoosePath)) continue;

                    string backupPath = Path.Combine(backupDir, $"{Path.GetFileName(looseFilePath)}.{Guid.NewGuid():N}.bak");
                    CopyBackupAndFlush(looseFilePath, backupPath);

                    string relativeToClient = Path.GetRelativePath(gameClientDirectory, looseFilePath).Replace('\\', '/');
                    overwrittenFiles.Add(new BackupFileInfo(relativeToClient, backupPath, string.Empty));
                    stagedWrites.Add((looseFilePath, content));
                }
            }

            if (!existedLoose)
            {
                string gameLoosePath = ModPackage.ResolveContainedDiskPath(gameDir, normRel);
                string relCreated = Path.GetRelativePath(gameClientDirectory, gameLoosePath).Replace('\\', '/');
                createdFiles.Add(relCreated);
                stagedWrites.Add((gameLoosePath, content));
            }
        }

        return stagedWrites;
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> EnsureModOptionsBundledAsync(
        IVirtualFileSystem? vfs,
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> preparedFiles,
        string excludedArchiveName,
        CancellationToken ct)
    {
        Dictionary<string, byte[]> files = new(preparedFiles, StringComparer.OrdinalIgnoreCase);

        var elemEntry = files.FirstOrDefault(kvp => kvp.Key.Equals("ui/mod_options_elements.package", StringComparison.OrdinalIgnoreCase));
        if (elemEntry.Value != null && elemEntry.Value.Length > 0)
        {
            string elementsText = GameResourceResolver.DecodeUtf8Text(elemEntry.Value);
            string innerElements = ExtractInnerElements(elementsText);

            string? baseModOpts;
            if (files.TryGetValue("ui/mod_options.package", out byte[]? existingBytes))
            {
                baseModOpts = GameResourceResolver.DecodeUtf8Text(existingBytes);
            }
            else
            {
                // Rollback has already restored loose directories to clean un-modded state;
                // resolve ui/mod_options.package across K2's true ClientModMountSpec hierarchy.
                using GameResourceResolver resolver = GameResourceResolver.CreateForClient(
                    gameClientDirectory,
                    vfs,
                    includeOverride: true,
                    excludedArchiveName: excludedArchiveName);
                baseModOpts = await resolver.TryReadGameTextAsync("ui/mod_options.package", ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(baseModOpts))
            {
                if (!innerElements.Contains("hbmod", StringComparison.OrdinalIgnoreCase))
                {
                    baseModOpts = Regex.Replace(baseModOpts, @"[ \t]*<instance\b[^>]*\bmod_id=""hbmod""[^>]*(?:/>|>[\s\S]*?</instance>)[ \t]*\r?\n?", string.Empty);
                }
                string patched = ReplaceIncludeTag(baseModOpts, innerElements);
                byte[] patchedBytes = Encoding.UTF8.GetBytes(patched);
                files["ui/mod_options.package"] = patchedBytes;
            }
        }

        return files;
    }

    private static string ExtractInnerElements(string xml)
    {
        string[] lines = xml.Split('\n');
        StringBuilder sb = new();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("<package>", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("</package>", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<!-- INSERT AFTER THIS -->", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                sb.AppendLine(line.TrimEnd('\r'));
            }
        }
        return sb.ToString();
    }

    private static string ReplaceIncludeTag(string baseXml, string innerElements)
    {
        const string target1 = "<include file=\"/ui/mod_options_elements.package\" />";
        if (baseXml.Contains(target1, StringComparison.OrdinalIgnoreCase))
        {
            return baseXml.Replace(target1, innerElements);
        }

        const string target2 = "<include file=\"ui/mod_options_elements.package\" />";
        if (baseXml.Contains(target2, StringComparison.OrdinalIgnoreCase))
        {
            return baseXml.Replace(target2, innerElements);
        }

        Match match = Regex.Match(baseXml, @"<include\s+file=""[^""]*mod_options_elements\.package""\s*/>", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return string.Concat(baseXml.AsSpan(0, match.Index), innerElements, baseXml.AsSpan(match.Index + match.Length));
        }

        return baseXml;
    }

    private static async Task SyncEffectiveFilesToLooseLocationsAsync(
        IVirtualFileSystem vfs,
        string gameClientDirectory,
        IReadOnlyDictionary<string, byte[]> effectiveFiles,
        List<string> createdFiles,
        List<BackupFileInfo> overwrittenFiles,
        CancellationToken ct)
    {
        string backupDir = GameResourceResolver.NormalizeVirtualPathSegments(gameClientDirectory, ".mod_backups");
        HashSet<string> updatedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, byte[]> kvp in effectiveFiles)
        {
            string relPath = kvp.Key.TrimStart('/');
            byte[] content = kvp.Value;

            bool existedLoose = false;
            foreach (string tierRel in LooseTiersShadowingGameArchives)
            {
                string loosePath = GameResourceResolver.NormalizeVirtualPathSegments(gameClientDirectory, tierRel, relPath);
                if (!vfs.FileExists(loosePath)
                    && string.IsNullOrEmpty(gameClientDirectory)
                    && tierRel.StartsWith("../", StringComparison.Ordinal))
                {
                    string stripped = GameResourceResolver.NormalizeVirtualPathSegments(tierRel[3..], relPath);
                    if (vfs.FileExists(stripped))
                    {
                        loosePath = stripped;
                    }
                }

                if (vfs.FileExists(loosePath))
                {
                    existedLoose = true;
                    if (!updatedPaths.Add(loosePath)) continue;

                    if (!vfs.DirectoryExists(backupDir)) vfs.CreateDirectory(backupDir);
                    string backupPath = $"{backupDir}/{Path.GetFileName(loosePath)}.{Guid.NewGuid():N}.bak";
                    byte[] origBytes = await vfs.ReadAllBytesAsync(loosePath, ct).ConfigureAwait(false);
                    await vfs.WriteAllBytesAsync(backupPath, origBytes, ct).ConfigureAwait(false);

                    string inGameRel = $"{tierRel}/{relPath}";
                    overwrittenFiles.Add(new BackupFileInfo(inGameRel, backupPath, string.Empty));

                    await vfs.WriteAllBytesAsync(loosePath, content, ct).ConfigureAwait(false);
                }
            }

            if (!existedLoose)
            {
                string gameLoosePath = GameResourceResolver.NormalizeVirtualPathSegments(gameClientDirectory, "game", relPath);
                string? parentDir = Path.GetDirectoryName(gameLoosePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(parentDir) && !vfs.DirectoryExists(parentDir))
                {
                    vfs.CreateDirectory(parentDir);
                }
                await vfs.WriteAllBytesAsync(gameLoosePath, content, ct).ConfigureAwait(false);
                createdFiles.Add($"game/{relPath}");
            }
        }
    }
}
