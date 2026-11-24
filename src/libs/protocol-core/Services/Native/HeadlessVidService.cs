// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessVidService.cs
// System: ProjectKongor - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 10 Dedicated Server Graphics Decoupling & Server Bloat Purge
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents metadata and purge/backup status of a server video bloat candidate.
/// </summary>
public sealed record ServerVidTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool HasBackup,
    bool HasBackupOld,
    bool IsPurged
);

/// <summary>
///     Aggregated report of dedicated server fleet video bloat status.
/// </summary>
public sealed record ServerVidReport(
    string TargetDirectory,
    bool IsFleetPurged,
    IReadOnlyList<ServerVidTarget> Targets,
    long TotalActiveBytes,
    long TotalSavedBytes,
    bool ServerProcessesRunning,
    string RunningProcesses
);

/// <summary>
///     Result of a server video bloat purge operation.
/// </summary>
public sealed record ServerVidPurgeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> PurgedPaths,
    IReadOnlyList<string> RotatedBakOldPaths,
    long BytesFreed
);

/// <summary>
///     Result of a server video bloat rollback operation.
/// </summary>
public sealed record ServerVidRollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string TargetTier,
    long BytesRestored
);

/// <summary>
///     Provides governance, safe bloat elimination, and two-tier rolling backups (.bak and .bak.old)
///     for dedicated server video rendering and presentation libraries (d3d9.dll, vid_d3d9_x64.dll, vid_gl2_x64.dll, dxvk.conf).
///     Strictly guarantees zero modification of the player client rendering subsystem (games/client/).
/// </summary>
public static class HeadlessVidService
{
    public static readonly string[] VideoBloatFilenames =
    [
        "d3d9.dll",
        "vid_d3d9_x64.dll",
        "vid_gl2_x64.dll",
        "dxvk.conf"
    ];

    private static readonly string[] ServerSubdirectories =
    [
        "",
        "windows-x64"
    ];

    /// <summary>
    ///     Finds all dedicated server video bloat targets within the given repository root or game folder.
    ///     Enforces strict guardrails preventing any client files from being included.
    /// </summary>
    public static IReadOnlyList<string> FindServerVidTargets(string repoRootOrGameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string repoRoot = ResolveRepoRoot(repoRootOrGameDir);
        bool isRepo = Directory.Exists(Path.Combine(repoRoot, "games"));

        string baseServerDir = isRepo
            ? Path.Combine(repoRoot, "games", "server")
            : repoRootOrGameDir;

        foreach (string sub in ServerSubdirectories)
        {
            string targetDir = string.IsNullOrEmpty(sub)
                ? baseServerDir
                : Path.Combine(baseServerDir, sub);

            foreach (string filename in VideoBloatFilenames)
            {
                string full = Path.GetFullPath(Path.Combine(targetDir, filename));
                if (File.Exists(full) || File.Exists(full + ".bak") || Directory.Exists(targetDir))
                {
                    if (IsServerSafeTarget(full))
                    {
                        set.Add(full);
                    }
                }
            }
        }

        return set.OrderBy(x => x).ToList();
    }

    /// <summary>
    ///     Inspects and aggregates video bloat status across dedicated server targets.
    /// </summary>
    public static ServerVidReport GetStatusReport(string repoRootOrGameDir)
    {
        var targets = FindServerVidTargets(repoRootOrGameDir);
        var items = new List<ServerVidTarget>();
        long totalActive = 0;
        long totalSaved = 0;
        bool isFleetPurged = targets.Count > 0;

        foreach (string full in targets)
        {
            string rel = Path.GetRelativePath(repoRootOrGameDir, full);
            bool exists = File.Exists(full);
            bool hasBak = File.Exists(full + ".bak");
            bool hasBakOld = File.Exists(full + ".bak.old");

            long size = 0;
            string sha = string.Empty;

            if (exists)
            {
                try
                {
                    var fi = new FileInfo(full);
                    size = fi.Length;
                    byte[] bytes = File.ReadAllBytes(full);
                    sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    totalActive += size;
                    isFleetPurged = false;
                }
                catch
                {
                    // In-use or locked
                }
            }
            else if (hasBak)
            {
                try
                {
                    var fi = new FileInfo(full + ".bak");
                    totalSaved += fi.Length;
                }
                catch
                {
                    // Suppress
                }
            }

            bool isPurged = !exists && hasBak;

            items.Add(new ServerVidTarget(
                rel,
                full,
                exists,
                size,
                sha,
                hasBak,
                hasBakOld,
                isPurged
            ));
        }

        bool procsRunning = NativeBinaryManager.IsGameOrServerRunning(out string runningProcs);
        return new ServerVidReport(repoRootOrGameDir, isFleetPurged, items, totalActive, totalSaved, procsRunning, runningProcs);
    }

    /// <summary>
    ///     Safely purges dedicated server video bloat across server targets.
    ///     Rotates active files to .bak (and existing .bak to .bak.old) with process checks.
    /// </summary>
    public static ServerVidPurgeResult PurgeServerVideoBloat(
        string repoRootOrGameDir,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot purge server video bloat while game client or server processes are active ({runningProcesses}). Close all instances or pass skipProcessCheck = true.");
        }

        var targets = FindServerVidTargets(repoRootOrGameDir);
        if (targets.Count == 0)
        {
            return new ServerVidPurgeResult(false, "No server video targets discovered.", [], [], 0);
        }

        var purgedPaths = new List<string>();
        var rotatedBakOldPaths = new List<string>();
        long bytesFreed = 0;

        foreach (string targetPath in targets)
        {
            if (!IsServerSafeTarget(targetPath))
            {
                logAction?.Invoke($"[VID_PURGE] Target '{targetPath}' is not an approved server target; skipping.");
                continue;
            }

            if (!File.Exists(targetPath))
            {
                logAction?.Invoke($"[VID_PURGE] Target '{targetPath}' is already purged or absent; skipping.");
                continue;
            }

            try
            {
                string bakPath = $"{targetPath}.bak";
                string bakOldPath = $"{targetPath}.bak.old";
                var fi = new FileInfo(targetPath);
                long fileSize = fi.Length;

                // Rotate existing .bak -> .bak.old
                if (File.Exists(bakPath))
                {
                    File.Copy(bakPath, bakOldPath, overwrite: true);
                    if (!rotatedBakOldPaths.Contains(bakOldPath))
                    {
                        rotatedBakOldPaths.Add(bakOldPath);
                    }
                    logAction?.Invoke($"[VID_PURGE] Rotated '{bakPath}' -> '{bakOldPath}'.");
                }

                // Backup active file -> .bak
                File.Copy(targetPath, bakPath, overwrite: true);
                logAction?.Invoke($"[VID_PURGE] Preserved active '{targetPath}' -> '{bakPath}'.");

                // Safely delete active target
                File.Delete(targetPath);
                purgedPaths.Add(targetPath);
                bytesFreed += fileSize;
                logAction?.Invoke($"[VID_PURGE] Successfully purged '{targetPath}' ({fileSize:N0} bytes).");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[VID_PURGE] Failed to purge '{targetPath}': {ex.Message}");
            }
        }

        bool success = purgedPaths.Count > 0 || targets.All(t => !File.Exists(t) && File.Exists(t + ".bak"));
        string msg = purgedPaths.Count > 0
            ? $"Successfully purged {purgedPaths.Count} server video bloat files (freed {bytesFreed:N0} bytes)."
            : "Server fleet video bloat is already purged.";

        return new ServerVidPurgeResult(success, msg, purgedPaths, rotatedBakOldPaths, bytesFreed);
    }

    /// <summary>
    ///     Rolls back video bloat across server targets to previous (.bak) or old (.bak.old) tier.
    /// </summary>
    public static ServerVidRollbackResult RollbackServerVideoBloat(
        string repoRootOrGameDir,
        string targetTier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot rollback video bloat while game client or server processes are active ({runningProcesses}). Close all instances or pass skipProcessCheck = true.");
        }

        var targets = FindServerVidTargets(repoRootOrGameDir);
        var restoredPaths = new List<string>();
        long bytesRestored = 0;

        string extension = targetTier.Equals("old", StringComparison.OrdinalIgnoreCase) ? ".bak.old" : ".bak";

        foreach (string targetPath in targets)
        {
            if (!IsServerSafeTarget(targetPath)) continue;

            string sourceBak = $"{targetPath}{extension}";
            if (!File.Exists(sourceBak))
            {
                // Fallback to .bak if .bak.old requested but absent
                if (extension == ".bak.old" && File.Exists($"{targetPath}.bak"))
                {
                    sourceBak = $"{targetPath}.bak";
                }
                else
                {
                    logAction?.Invoke($"[VID_ROLLBACK] No backup found for '{targetPath}' with tier '{targetTier}'.");
                    continue;
                }
            }

            try
            {
                File.Copy(sourceBak, targetPath, overwrite: true);
                restoredPaths.Add(targetPath);
                var fi = new FileInfo(targetPath);
                bytesRestored += fi.Length;
                logAction?.Invoke($"[VID_ROLLBACK] Restored '{targetPath}' from '{sourceBak}' ({fi.Length:N0} bytes).");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[VID_ROLLBACK] Failed to rollback '{targetPath}': {ex.Message}");
            }
        }

        bool success = restoredPaths.Count > 0;
        string msg = success
            ? $"Successfully restored {restoredPaths.Count} server video binaries ({bytesRestored:N0} bytes) from '{targetTier}' backup."
            : $"No server video binaries restored from '{targetTier}' tier.";

        return new ServerVidRollbackResult(success, msg, restoredPaths, targetTier, bytesRestored);
    }

    /// <summary>
    ///     Strict invariant check ensuring path targets server and NEVER touches client files.
    /// </summary>
    public static bool IsServerSafeTarget(string path)
    {
        string normalized = path.Replace('/', '\\');
        if (normalized.Contains(@"\games\client\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\client\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!normalized.Contains(@"\server\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(normalized);
        if (fileName.EndsWith(".bak.old", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^8];
        }
        else if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return VideoBloatFilenames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveRepoRoot(string repoRootOrGameDir)
    {
        var dir = new DirectoryInfo(repoRootOrGameDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "server")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return repoRootOrGameDir;
    }
}
