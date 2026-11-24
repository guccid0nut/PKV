// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace WILLOWMAKER.Core.Services;

/// <summary>
///     Audit item describing the status of a file in the VFS override stack.
/// </summary>
public sealed class VfsAuditItem
{
    public string VfsPath { get; set; } = string.Empty;
    public string PhysicalPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public bool IsShadowingArchive { get; set; }
    public string? ShadowedArchiveName { get; set; }
    public bool HasPathWarning { get; set; }
    public string? PathWarningMessage { get; set; }
}

/// <summary>
///     Summary report of the VFS override audit.
/// </summary>
public sealed class VfsAuditReport
{
    public int TotalLooseFiles { get; set; }
    public int TotalShadowedFiles { get; set; }
    public int TotalWarnings { get; set; }
    public List<VfsAuditItem> Items { get; set; } = [];
    public List<string> ScannedArchives { get; set; } = [];
}

/// <summary>
///     Service that audits loose files in 'override/' against retail '.s2z' archives to ensure
///     zero archive leakage, verify 100% loose file priority, and detect structural path mistakes.
/// </summary>
public sealed class VfsAuditService(ILogger<VfsAuditService> logger)
{
    private readonly ILogger<VfsAuditService> _logger = logger;

    /// <summary>
    ///     Performs a full VFS audit on the specified game client root or active directories.
    /// </summary>
    public VfsAuditReport PerformAudit(string clientRoot)
    {
        VfsAuditReport report = new();
        string overrideDir = Path.Combine(clientRoot, "override");
        string gameDir = Path.Combine(clientRoot, "game");

        if (!Directory.Exists(overrideDir))
        {
            _logger.LogWarning("[VfsAudit] Override directory does not exist at: {Dir}", overrideDir);
            return report;
        }

        // 1. Index all files within archives (.s2z) in game/
        Dictionary<string, string> archiveEntries = new(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(gameDir))
        {
            string[] s2zFiles = Directory.GetFiles(gameDir, "*.s2z", SearchOption.TopDirectoryOnly);
            foreach (string s2z in s2zFiles)
            {
                string archiveName = Path.GetFileName(s2z);
                report.ScannedArchives.Add(archiveName);

                try
                {
                    using ZipArchive zip = ZipFile.OpenRead(s2z);
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string normalized = "/" + entry.FullName.Replace('\\', '/').TrimStart('/');
                        if (!archiveEntries.ContainsKey(normalized)) archiveEntries[normalized] = archiveName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VfsAudit] Could not read archive: {Archive}", archiveName);
                }
            }
        }

        // 2. Scan all loose files in override/
        string[] looseFiles = Directory.GetFiles(overrideDir, "*.*", SearchOption.AllDirectories);
        foreach (string file in looseFiles)
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith(".hotreload", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                file.Contains(".git", StringComparison.OrdinalIgnoreCase))
                continue;

            FileInfo info = new(file);
            string relative = Path.GetRelativePath(overrideDir, file).Replace('\\', '/');
            string vfsPath = "/" + relative.TrimStart('/');

            VfsAuditItem item = new()
            {
                VfsPath = vfsPath,
                PhysicalPath = file,
                FileSizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc
            };

            // Path hazard checks
            if (relative.StartsWith("override/", StringComparison.OrdinalIgnoreCase))
            {
                item.HasPathWarning = true;
                item.PathWarningMessage =
                    "PowerShell Nesting Hazard: redundant 'override/override' directory prefix detected.";
                report.TotalWarnings++;
            }
            else if (relative.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            {
                item.HasPathWarning = true;
                item.PathWarningMessage =
                    "Mod Root Disalignment: asset placed in 'override/game/' instead of 'override/'.";
                report.TotalWarnings++;
            }

            // Check if this loose file shadows a retail archive entry
            if (archiveEntries.TryGetValue(vfsPath, out string? shadowedArchive))
            {
                item.IsShadowingArchive = true;
                item.ShadowedArchiveName = shadowedArchive;
                report.TotalShadowedFiles++;
            }

            report.Items.Add(item);
            report.TotalLooseFiles++;
        }

        _logger.LogInformation(
            "[VfsAudit] Complete. Loose Files: {Loose}, Shadowing Retail Archives: {Shadowed}, Warnings: {Warnings}",
            report.TotalLooseFiles, report.TotalShadowedFiles, report.TotalWarnings);

        return report;
    }

    /// <summary>
    ///     Synchronizes critical loose override files into the underlying .s2z archives so that
    ///     early-boot engine reads (which may bypass loose override folders) always load updated code.
    /// </summary>
    public int SyncOverridesToArchives(string clientRoot)
    {
        string overrideDir = Path.Combine(clientRoot, "override");
        string gameDir = Path.Combine(clientRoot, "game");

        if (!Directory.Exists(overrideDir) || !Directory.Exists(gameDir))
            return 0;

        int syncedCount = 0;
        string[] s2zFiles = Directory.GetFiles(gameDir, "*.s2z", SearchOption.TopDirectoryOnly);

        foreach (string s2z in s2zFiles)
            try
            {
                using ZipArchive zip = ZipFile.Open(s2z, ZipArchiveMode.Update);
                List<ZipArchiveEntry> entriesToUpdate = [];

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string relativePath = entry.FullName.Replace('\\', '/').TrimStart('/');
                    string physicalPath =
                        Path.Combine(overrideDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(physicalPath)) entriesToUpdate.Add(entry);
                }

                foreach (ZipArchiveEntry oldEntry in entriesToUpdate)
                {
                    string entryName = oldEntry.FullName;
                    string relativePath = entryName.Replace('\\', '/').TrimStart('/');
                    string physicalPath =
                        Path.Combine(overrideDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

                    byte[] newBytes = File.ReadAllBytes(physicalPath);
                    oldEntry.Delete();

                    ZipArchiveEntry newEntry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using Stream stream = newEntry.Open();
                    stream.Write(newBytes, 0, newBytes.Length);

                    _logger.LogInformation("[VfsAudit] Synced loose override {Path} into archive {Archive}",
                        relativePath, Path.GetFileName(s2z));
                    syncedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VfsAudit] Failed to sync overrides into archive {Archive}",
                    Path.GetFileName(s2z));
            }

        return syncedCount;
    }
}
