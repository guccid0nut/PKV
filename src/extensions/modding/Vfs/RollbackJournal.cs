// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Serialization;
using NEXUS.Contracts.Vfs;

namespace Kongor.Modding.Vfs;

[JsonConverter(typeof(JsonStringEnumConverter<InjectionMode>))]
public enum InjectionMode
{
    Vfs,
    Archive,
    Direct
}

public sealed record JournalEntry
{
    public DateTimeOffset AppliedTimestampUtc { get; init; } = TimeProvider.System.GetUtcNow();
    public InjectionMode Mode { get; init; }
    public string TargetDirectory { get; init; } = string.Empty;
    public List<AppliedModInfo> AppliedMods { get; init; } = [];
    public List<string> CreatedFiles { get; init; } = [];
    public List<BackupFileInfo> OverwrittenFiles { get; init; } = [];
}

public sealed record AppliedModInfo(string Name, string Version, string? Hash = null);

public sealed record BackupFileInfo(string RelativePath, string BackupFilePath, string OriginalSha256);

/// <summary>
///     Transaction journal providing atomic restore and rollback of applied mods.
/// </summary>
public static class RollbackJournal
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new() { WriteIndented = true };
    private const string JournalFileName = "rollback_journal.json";

    public static string GetJournalPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, JournalFileName);
    }

    public static string GetJournalVirtualPath(string baseDirectory)
    {
        return string.IsNullOrEmpty(baseDirectory)
            ? JournalFileName
            : $"{baseDirectory.TrimEnd('/')}/{JournalFileName}";
    }

    public static bool HasActiveModifications(string baseDirectory)
    {
        string path = GetJournalPath(baseDirectory);
        return File.Exists(path);
    }

    public static bool HasActiveModifications(IVirtualFileSystem vfs, string baseDirectory = "")
    {
        string path = GetJournalVirtualPath(baseDirectory);
        return vfs.FileExists(path);
    }

    public static async Task<JournalEntry?> LoadJournalAsync(string baseDirectory, CancellationToken ct = default)
    {
        string path = GetJournalPath(baseDirectory);
        if (!File.Exists(path)) return null;

        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, ModdingJsonSerializerContext.Default.JournalEntry);
    }

    public static async Task<JournalEntry?> LoadJournalAsync(IVirtualFileSystem vfs, string baseDirectory = "",
        CancellationToken ct = default)
    {
        string path = GetJournalVirtualPath(baseDirectory);
        if (!vfs.FileExists(path)) return null;

        string json = await vfs.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, ModdingJsonSerializerContext.Default.JournalEntry);
    }

    public static async Task SaveJournalAsync(string baseDirectory, JournalEntry entry, CancellationToken ct = default)
    {
        string path = GetJournalPath(baseDirectory);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(entry, ModdingJsonSerializerContext.Default.JournalEntry);
        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(json);
        string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        await using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await fs.WriteAsync(utf8Bytes, ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, true);
    }

    public static async Task SaveJournalAsync(IVirtualFileSystem vfs, string baseDirectory, JournalEntry entry,
        CancellationToken ct = default)
    {
        string path = GetJournalVirtualPath(baseDirectory);
        string json = JsonSerializer.Serialize(entry, ModdingJsonSerializerContext.Default.JournalEntry);
        await vfs.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    public static async Task<bool> ExecuteRollbackAsync(string baseDirectory, CancellationToken ct = default)
    {
        JournalEntry? journal = await LoadJournalAsync(baseDirectory, ct).ConfigureAwait(false);
        if (journal == null)
        {
            string triggerFile = Path.Combine(baseDirectory, "override", ".hotreload.trigger");
            if (File.Exists(triggerFile))
            {
                try { File.Delete(triggerFile); } catch { /* ignore */ }
            }
            string res999 = Path.Combine(baseDirectory, "game", "resources999.s2z");
            if (File.Exists(res999))
            {
                try { File.Delete(res999); } catch { /* ignore if in use by running game client */ }
            }
            CleanResidualModOptions(baseDirectory);
            return true;
        }

        List<BackupFileInfo> failedRestores = [];
        List<string> failedDeletes = [];
        HashSet<string> overwrittenNormalizedPaths = new HashSet<string>(
            journal.OverwrittenFiles.Select(b => b.RelativePath.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        // 1. Restore overwritten files from backup (preserving backup + WAL entry if target is locked)
        foreach (BackupFileInfo backup in journal.OverwrittenFiles)
        {
            string targetPath =
                Path.GetFullPath(Path.Combine(baseDirectory, backup.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(backup.BackupFilePath))
            {
                Console.Error.WriteLine(
                    $"[RollbackJournal][WARN] Backup file '{backup.BackupFilePath}' missing while restoring '{targetPath}'.");
                continue;
            }

            try
            {
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backup.BackupFilePath, targetPath, true);
                try
                {
                    File.Delete(backup.BackupFilePath);
                }
                catch (Exception delEx)
                {
                    Console.Error.WriteLine(
                        $"[RollbackJournal][WARN] Restored '{targetPath}' but could not delete consumed backup '{backup.BackupFilePath}': {delEx.Message}");
                }
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"[RollbackJournal][WARN] Failed to restore locked file '{targetPath}' from '{backup.BackupFilePath}': {restoreEx.Message}. Preserving backup and WAL entry for retry.");
                failedRestores.Add(backup);
            }
        }

        // 2. Delete all created files (skipping any path that was an overwritten baseline!)
        foreach (string createdRel in journal.CreatedFiles)
        {
            string normCreated = createdRel.Replace('\\', '/');
            if (overwrittenNormalizedPaths.Contains(normCreated))
            {
                continue;
            }

            string targetPath =
                Path.GetFullPath(Path.Combine(baseDirectory, createdRel.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(targetPath);
                }
                catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"[RollbackJournal][WARN] Failed to delete created file '{targetPath}': {delEx.Message}. Retaining WAL entry for retry.");
                    failedDeletes.Add(createdRel);
                }
            }
        }

        // 3. Clean up empty override directories if any
        string overrideFolder = Path.Combine(baseDirectory, "override");
        if (Directory.Exists(overrideFolder))
        {
            CleanEmptyDirectories(overrideFolder);
            if (Directory.Exists(overrideFolder) && !Directory.EnumerateFileSystemEntries(overrideFolder).Any())
            {
                try
                {
                    Directory.Delete(overrideFolder, true);
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        // 4. Delete game/resources999.s2z ONLY if it was not a pre-existing overwritten archive restored in step 1
        if (!overwrittenNormalizedPaths.Contains("game/resources999.s2z"))
        {
            string res999Path = Path.Combine(baseDirectory, "game", "resources999.s2z");
            if (File.Exists(res999Path))
            {
                try
                {
                    File.Delete(res999Path);
                }
                catch (Exception delArchiveEx) when (delArchiveEx is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"[RollbackJournal][WARN] Could not delete '{res999Path}': {delArchiveEx.Message}.");
                    if (!failedDeletes.Contains("game/resources999.s2z", StringComparer.OrdinalIgnoreCase))
                    {
                        failedDeletes.Add("game/resources999.s2z");
                    }
                }
            }
        }

        // 5. If any restore or delete failed, preserve .mod_backups and write residual WAL journal for retry!
        if (failedRestores.Count > 0 || failedDeletes.Count > 0)
        {
            JournalEntry residualJournal = journal with
            {
                OverwrittenFiles = failedRestores,
                CreatedFiles = failedDeletes
            };
            await SaveJournalAsync(baseDirectory, residualJournal, ct).ConfigureAwait(false);
            Console.Error.WriteLine(
                $"[RollbackJournal][WAL] Partial rollback on '{baseDirectory}': {failedRestores.Count} restore(s) and {failedDeletes.Count} delete(s) pending retry; backups preserved.");
            return false;
        }

        // 6. All files cleanly restored/removed — safe to remove .mod_backups and WAL journal
        string backupFolder = Path.Combine(baseDirectory, ".mod_backups");
        if (Directory.Exists(backupFolder))
        {
            try
            {
                Directory.Delete(backupFolder, true);
            }
            catch
            {
                /* ignore */
            }
        }

        string journalPath = GetJournalPath(baseDirectory);
        if (File.Exists(journalPath)) File.Delete(journalPath);

        // 7. Clean residual mod options across loose UI directories
        CleanResidualModOptions(baseDirectory);

        return true;
    }

    public static async Task<bool> ExecuteRollbackAsync(IVirtualFileSystem vfs, string baseDirectory = "",
        CancellationToken ct = default)
    {
        JournalEntry? journal = await LoadJournalAsync(vfs, baseDirectory, ct).ConfigureAwait(false);
        if (journal == null)
        {
            string overrideDir = string.IsNullOrEmpty(baseDirectory)
                ? "override"
                : $"{baseDirectory.TrimEnd('/')}/override";
            string triggerFile = $"{overrideDir}/.hotreload.trigger";
            if (vfs.FileExists(triggerFile))
            {
                try { vfs.DeleteFile(triggerFile); } catch { /* ignore */ }
            }
            string res999 = string.IsNullOrEmpty(baseDirectory)
                ? "game/resources999.s2z"
                : $"{baseDirectory.TrimEnd('/')}/game/resources999.s2z";
            if (vfs.FileExists(res999))
            {
                try { vfs.DeleteFile(res999); } catch { /* ignore */ }
            }
            CleanResidualModOptions(baseDirectory);
            return true;
        }

        List<BackupFileInfo> failedRestores = [];
        List<string> failedDeletes = [];
        HashSet<string> overwrittenNormalizedPaths = new HashSet<string>(
            journal.OverwrittenFiles.Select(b => b.RelativePath.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        // 1. Restore overwritten files from backup
        foreach (BackupFileInfo backup in journal.OverwrittenFiles)
        {
            if (!vfs.FileExists(backup.BackupFilePath))
            {
                continue;
            }

            try
            {
                string targetPath = GameResourceResolver.NormalizeVirtualPathSegments(baseDirectory, backup.RelativePath);
                byte[] backupData = await vfs.ReadAllBytesAsync(backup.BackupFilePath, ct).ConfigureAwait(false);
                await vfs.WriteAllBytesAsync(targetPath, backupData, ct).ConfigureAwait(false);
                vfs.DeleteFile(backup.BackupFilePath);
            }
            catch (Exception restoreEx)
            {
                Console.Error.WriteLine(
                    $"[RollbackJournal][WARN] VFS restore failed for '{backup.RelativePath}': {restoreEx.Message}. Preserving backup.");
                failedRestores.Add(backup);
            }
        }

        // 2. Delete all created files
        foreach (string createdRel in journal.CreatedFiles)
        {
            if (overwrittenNormalizedPaths.Contains(createdRel.Replace('\\', '/')))
            {
                continue;
            }

            string targetPath = GameResourceResolver.NormalizeVirtualPathSegments(baseDirectory, createdRel);
            if (vfs.FileExists(targetPath))
            {
                try
                {
                    vfs.DeleteFile(targetPath);
                }
                catch (Exception delEx)
                {
                    Console.Error.WriteLine(
                        $"[RollbackJournal][WARN] VFS delete failed for '{targetPath}': {delEx.Message}.");
                    failedDeletes.Add(createdRel);
                }
            }
        }

        if (failedRestores.Count > 0 || failedDeletes.Count > 0)
        {
            JournalEntry residualJournal = journal with
            {
                OverwrittenFiles = failedRestores,
                CreatedFiles = failedDeletes
            };
            await SaveJournalAsync(vfs, baseDirectory, residualJournal, ct).ConfigureAwait(false);
            return false;
        }

        // 3. Delete journal file
        string journalPath = GetJournalVirtualPath(baseDirectory);
        if (vfs.FileExists(journalPath)) vfs.DeleteFile(journalPath);

        // 4. Clean residual mod options across loose UI directories
        CleanResidualModOptions(baseDirectory);

        return true;
    }

    private static void CleanEmptyDirectories(string startLocation)
    {
        foreach (string directory in Directory.GetDirectories(startLocation))
        {
            CleanEmptyDirectories(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                try
                {
                    Directory.Delete(directory, false);
                }
                catch
                {
                    /* ignore */
                }
        }
    }

    public static void CleanResidualModOptions(string baseDirectory)
    {
        try
        {
            string[] searchRoots =
            [
                Path.Combine(baseDirectory, "game", "ui"),
                Path.Combine(baseDirectory, "override", "ui"),
                Path.Combine(baseDirectory, "..", "shared", "ui"),
                Path.Combine(baseDirectory, "ClientPackageFiles", "ui"),
                Path.Combine(baseDirectory, "games", "client", "game", "ui"),
                Path.Combine(baseDirectory, "games", "client", "override", "ui"),
                Path.Combine(baseDirectory, "games", "shared", "ui"),
                Path.Combine(baseDirectory, "games", "client", "ClientPackageFiles", "ui")
            ];

            foreach (string uiDir in searchRoots)
            {
                if (!Directory.Exists(uiDir)) continue;

                string modOptsPath = Path.Combine(uiDir, "mod_options.package");
                if (File.Exists(modOptsPath))
                {
                    string text = File.ReadAllText(modOptsPath);
                    if (text.Contains("hbmod", StringComparison.OrdinalIgnoreCase))
                    {
                        string cleaned = Regex.Replace(text, @"[ \t]*<instance\b[^>]*\bmod_id=""hbmod""[^>]*(?:/>|>[\s\S]*?</instance>)[ \t]*\r?\n?", string.Empty);
                        if (!cleaned.Contains("mod_options_elements.package", StringComparison.OrdinalIgnoreCase))
                        {
                            cleaned = Regex.Replace(cleaned, @"(\t*<!-- stuff from all other mods -->[\r\n]+)", "$1\t\t\t\t\t<include file=\"/ui/mod_options_elements.package\" />\r\n");
                        }
                        File.WriteAllText(modOptsPath, cleaned);
                    }
                }

                string modElemPath = Path.Combine(uiDir, "mod_options_elements.package");
                if (File.Exists(modElemPath))
                {
                    string text = File.ReadAllText(modElemPath);
                    if (text.Contains("hbmod", StringComparison.OrdinalIgnoreCase))
                    {
                        string cleanElements = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<package>\r\n\t<!-- INSERT AFTER THIS -->\r\n\t\r\n</package>\r\n";
                        File.WriteAllText(modElemPath, cleanElements);
                    }
                }

                string[] interfaceNames = ["game_apex.interface", "game_legacy.interface", "game_garena_other.interface"];
                foreach (string ifaceName in interfaceNames)
                {
                    string ifacePath = Path.Combine(uiDir, ifaceName);
                    if (File.Exists(ifacePath))
                    {
                        string text = File.ReadAllText(ifacePath);
                        if (text.Contains("heartbeat_overlay", StringComparison.OrdinalIgnoreCase))
                        {
                            // Native heartbeat feature is present: only clean legacy hb_main if present
                            if (text.Contains("hb_main", StringComparison.OrdinalIgnoreCase))
                            {
                                string cleaned = Regex.Replace(text, @"[ \t]*<instance\s+name=""hb_main""\s*/>[ \t]*\r?\n?", string.Empty);
                                File.WriteAllText(ifacePath, cleaned);
                            }
                        }
                        else if (text.Contains("heartbeat.package", StringComparison.OrdinalIgnoreCase) || text.Contains("hb_main", StringComparison.OrdinalIgnoreCase))
                        {
                            string cleaned = Regex.Replace(text, @"[ \t]*<include\s+file=""(/ui/)?heartbeat\.package""\s*/>[ \t]*\r?\n?", string.Empty);
                            cleaned = Regex.Replace(cleaned, @"[ \t]*<instance\s+name=""hb_main""\s*/>[ \t]*\r?\n?", string.Empty);
                            File.WriteAllText(ifacePath, cleaned);
                        }
                    }
                }

                string hbPackage = Path.Combine(uiDir, "heartbeat.package");
                if (File.Exists(hbPackage))
                {
                    try
                    {
                        string pkgContent = File.ReadAllText(hbPackage);
                        if (!pkgContent.Contains("heartbeat_overlay", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(hbPackage);
                        }
                    }
                    catch { }
                }

                string hbmodDir = Path.Combine(uiDir, "hbmod");
                if (Directory.Exists(hbmodDir))
                {
                    try { Directory.Delete(hbmodDir, true); } catch { }
                }
            }
        }
        catch
        {
            // Non-critical cleanup
        }
    }
}
