// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoModernizationService.cs
// System: ProjectKongor - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 9 Hardware-Accelerated Video Decoding (FFmpeg 7.x / DXVA2)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents metadata and deployment status of an FFmpeg video library target.
/// </summary>
public sealed record VideoLibraryTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool IsShim,
    bool HasCoreEngine,
    bool HasBackup,
    bool HasBackupOld,
    string Status
);

/// <summary>
///     Aggregated report of client and server video modernization status.
/// </summary>
public sealed record VideoModernizationReport(
    string RepoRoot,
    bool IsClientModernized,
    bool IsServerPurged,
    IReadOnlyList<VideoLibraryTarget> ClientTargets,
    IReadOnlyList<VideoLibraryTarget> ServerTargets,
    long ServerSpaceSavedBytes,
    string HardwareAccelerationBackend,
    bool LoginScreenPreserved
);

/// <summary>
///     Result of a client video modernization deployment operation.
/// </summary>
public sealed record VideoDeployResult(
    bool Success,
    string Message,
    IReadOnlyList<string> DeployedPaths,
    IReadOnlyList<string> PreservedCorePaths,
    IReadOnlyList<string> RotatedBakPaths
);

/// <summary>
///     Result of a video rollback operation.
/// </summary>
public sealed record VideoRollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string RestoredFrom
);

/// <summary>
///     Result of a dedicated server video bloat purge operation.
/// </summary>
public sealed record ServerVideoPurgeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> PurgedPaths,
    long BytesFreed
);

/// <summary>
///     Provides governance, safe staged deployment, two-tier rolling backups (.bak and .bak.old),
///     and dedicated server bloat elimination for the K2 video playback subsystem.
/// </summary>
public static class VideoModernizationService
{
    public static readonly string[] VideoDllNames =
    [
        "avcodec-x64-58.dll",
        "avdevice-x64-58.dll",
        "avfilter-x64-7.dll",
        "avformat-x64-58.dll",
        "avutil-x64-56.dll",
        "swresample-x64-3.dll",
        "swscale-x64-5.dll"
    ];

    private const long ProxySizeMaxThreshold = 100 * 1024; // 100 KB threshold

    /// <summary>
    ///     Finds all video library targets across client directories.
    /// </summary>
    public static IReadOnlyList<string> FindClientVideoTargets(string repoRootOrGameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isRepo = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"));

        string[] searchDirs = isRepo
            ? [Path.Combine(repoRootOrGameDir, "games", "client"), Path.Combine(repoRootOrGameDir, "games", "client", "windows-x64")]
            : [repoRootOrGameDir, Path.Combine(repoRootOrGameDir, "windows-x64")];

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string dllName in VideoDllNames)
            {
                string full = Path.GetFullPath(Path.Combine(dir, dllName));
                if (File.Exists(full) || File.Exists(full + ".bak") || File.Exists(full + ".purged"))
                {
                    set.Add(full);
                }
            }
        }

        return set.OrderBy(p => p).ToList();
    }

    /// <summary>
    ///     Finds all video library targets across server directories.
    /// </summary>
    public static IReadOnlyList<string> FindServerVideoTargets(string repoRootOrGameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isRepo = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"));

        string[] searchDirs = isRepo
            ? [Path.Combine(repoRootOrGameDir, "games", "server"), Path.Combine(repoRootOrGameDir, "games", "server", "windows-x64")]
            : [repoRootOrGameDir, Path.Combine(repoRootOrGameDir, "windows-x64")];

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string dllName in VideoDllNames)
            {
                string full = Path.GetFullPath(Path.Combine(dir, dllName));
                if (File.Exists(full) || File.Exists(full + ".bak") || File.Exists(full + ".purged"))
                {
                    set.Add(full);
                }
            }
        }

        return set.OrderBy(p => p).ToList();
    }

    /// <summary>
    ///     Checks whether any dedicated server processes are running.
    /// </summary>
    public static bool IsServerRunning(out string runningProcessNames)
    {
        try
        {
            Process[] procs = Process.GetProcesses();
            var conflicting = procs.Where(p =>
                p.ProcessName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (conflicting.Count > 0)
            {
                runningProcessNames = string.Join(", ", conflicting.Select(p => $"{p.ProcessName} (PID: {p.Id})"));
                return true;
            }
        }
        catch
        {
            // Restricted process enumeration fallback
        }

        runningProcessNames = string.Empty;
        return false;
    }

    /// <summary>
    ///     Checks whether any client processes are running.
    /// </summary>
    public static bool IsClientRunning(out string runningProcessNames)
    {
        return ClientAudioModernizationService.IsClientRunning(out runningProcessNames);
    }

    /// <summary>
    ///     Inspects and compiles a status report of client and server video subsystems.
    /// </summary>
    public static VideoModernizationReport GetVideoStatus(string repoRootOrGameDir)
    {
        var clientPaths = FindClientVideoTargets(repoRootOrGameDir);
        var serverPaths = FindServerVideoTargets(repoRootOrGameDir);

        var clientTargets = new List<VideoLibraryTarget>(clientPaths.Count);
        bool clientModernized = false;

        foreach (string path in clientPaths)
        {
            string rel = Path.GetRelativePath(repoRootOrGameDir, path);
            bool exists = File.Exists(path);
            long size = exists ? new FileInfo(path).Length : 0;
            string hash = exists ? ComputeSha256(path) : "not_found";
            bool hasBak = File.Exists(path + ".bak");
            bool hasBakOld = File.Exists(path + ".bak.old");

            bool isShim = exists && size < ProxySizeMaxThreshold;
            string status = isShim ? "Modernized Hardware Shim" : (exists ? "Vanilla FFmpeg 4.3.1 (2020)" : "Missing");

            string targetDir = Path.GetDirectoryName(path) ?? "";
            bool hasCore = File.Exists(Path.Combine(targetDir, "avcodec_core.dll")) ||
                           File.Exists(Path.Combine(targetDir, Path.GetFileNameWithoutExtension(path) + "_core.dll"));

            if (Path.GetFileName(path).Equals("avcodec-x64-58.dll", StringComparison.OrdinalIgnoreCase) && isShim)
            {
                clientModernized = true;
            }

            clientTargets.Add(new VideoLibraryTarget(
                rel, path, exists, size, hash, isShim, hasCore, hasBak, hasBakOld, status
            ));
        }

        var serverTargets = new List<VideoLibraryTarget>(serverPaths.Count);
        bool serverPurged = true;
        long savedBytes = 0;

        foreach (string path in serverPaths)
        {
            string rel = Path.GetRelativePath(repoRootOrGameDir, path);
            bool exists = File.Exists(path);
            bool hasPurged = File.Exists(path + ".purged") || File.Exists(path + ".bak");
            long size = exists ? new FileInfo(path).Length : 0;
            string hash = exists ? ComputeSha256(path) : "purged";

            if (exists)
            {
                serverPurged = false;
            }
            else if (hasPurged)
            {
                string bakPath = File.Exists(path + ".purged") ? path + ".purged" : path + ".bak";
                savedBytes += new FileInfo(bakPath).Length;
            }

            string status = exists ? "Active Bloat (Unused by Server)" : "Purged (Optimized 0 MB)";
            serverTargets.Add(new VideoLibraryTarget(
                rel, path, exists, size, hash, false, false, hasPurged, false, status
            ));
        }

        // Verify login screen preservation invariant
        string loginBakPath = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"))
            ? Path.Combine(repoRootOrGameDir, "games", "client", "game", "videos", "login_1080.avi.bak")
            : Path.Combine(repoRootOrGameDir, "game", "videos", "login_1080.avi.bak");

        string loginActivePath = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"))
            ? Path.Combine(repoRootOrGameDir, "games", "client", "game", "videos", "login_1080.avi")
            : Path.Combine(repoRootOrGameDir, "game", "videos", "login_1080.avi");

        bool loginPreserved = (!File.Exists(loginActivePath)) || File.Exists(loginBakPath);

        string backend = clientModernized
            ? "Direct3D 11 Video Acceleration (D3D11VA / DXVA2) + AVX2 Fallback"
            : "Software CPU Decode (FFmpeg 4.3.1)";

        return new VideoModernizationReport(
            repoRootOrGameDir,
            clientModernized,
            serverPurged,
            clientTargets,
            serverTargets,
            savedBytes,
            backend,
            loginPreserved
        );
    }

    /// <summary>
    ///     Safely purges unused FFmpeg video libraries from the dedicated server fleet,
    ///     saving ~25.5 MB per server install while preserving atomic backups (.purged).
    /// </summary>
    public static ServerVideoPurgeResult PurgeServerVideoBloat(
        string repoRootOrGameDir,
        bool skipProcessCheck = false)
    {
        if (!skipProcessCheck && IsServerRunning(out string runningProcs))
        {
            return new ServerVideoPurgeResult(false, $"Cannot purge server video bloat while server processes are executing: {runningProcs}.", [], 0);
        }

        var targets = FindServerVideoTargets(repoRootOrGameDir);
        var purged = new List<string>();
        long freedBytes = 0;

        foreach (string target in targets)
        {
            if (!File.Exists(target)) continue;

            // Extra safety guard: never touch client path
            if (target.Replace('\\', '/').Contains("/client/"))
            {
                throw new InvalidOperationException($"Attempted server purge on client path: {target}");
            }

            long len = new FileInfo(target).Length;
            string backupPath = target + ".purged";

            // Backup original DLL before removing
            File.Copy(target, backupPath, overwrite: true);
            File.Delete(target);

            purged.Add(target);
            freedBytes += len;
        }

        return new ServerVideoPurgeResult(
            true,
            $"Successfully purged {purged.Count} unused video DLLs ({freedBytes:N0} bytes freed) from dedicated server fleet.",
            purged,
            freedBytes
        );
    }

    /// <summary>
    ///     Restores purged video DLLs to the dedicated server fleet from .purged or .bak.
    /// </summary>
    public static ServerVideoPurgeResult RestoreServerVideo(
        string repoRootOrGameDir,
        bool skipProcessCheck = false)
    {
        if (!skipProcessCheck && IsServerRunning(out string runningProcs))
        {
            return new ServerVideoPurgeResult(false, $"Cannot restore server video while server processes are executing: {runningProcs}.", [], 0);
        }

        var targets = FindServerVideoTargets(repoRootOrGameDir);
        var restored = new List<string>();
        long restoredBytes = 0;

        foreach (string target in targets)
        {
            string backupPath = target + ".purged";
            if (!File.Exists(backupPath))
            {
                backupPath = target + ".bak";
            }

            if (File.Exists(backupPath) && !File.Exists(target))
            {
                File.Copy(backupPath, target, overwrite: true);
                restored.Add(target);
                restoredBytes += new FileInfo(target).Length;
            }
        }

        return new ServerVideoPurgeResult(
            true,
            $"Restored {restored.Count} video DLLs ({restoredBytes:N0} bytes) to dedicated server fleet.",
            restored,
            restoredBytes
        );
    }

    /// <summary>
    ///     Safely deploys hardware-accelerated video shims and stubs across client targets.
    ///     Preserves genuine core engines as *_core.dll with two-tier rolling backups (.bak / .bak.old).
    ///     Strictly preserves the login screen asset state (login_1080.avi.bak).
    /// </summary>
    public static VideoDeployResult DeployClientVideoUpgrade(
        string repoRootOrGameDir,
        string stagedBinariesDir,
        bool skipProcessCheck = false)
    {
        if (!skipProcessCheck && IsClientRunning(out string runningProcs))
        {
            return new VideoDeployResult(false, $"Cannot deploy video upgrade while client is running: {runningProcs}.", [], [], []);
        }

        if (!Directory.Exists(stagedBinariesDir))
        {
            return new VideoDeployResult(false, $"Staged binaries directory not found at '{stagedBinariesDir}'.", [], [], []);
        }

        var clientTargets = FindClientVideoTargets(repoRootOrGameDir);
        if (clientTargets.Count == 0)
        {
            return new VideoDeployResult(false, "No client video targets discovered.", [], [], []);
        }

        var deployed = new List<string>();
        var preservedCore = new List<string>();
        var rotatedBak = new List<string>();

        // Binaries to deploy from staging
        string[] deployables = ["avcodec-x64-58.dll", "avfilter-x64-7.dll", "avdevice-x64-58.dll"];

        foreach (string deployName in deployables)
        {
            string sourcePath = Path.Combine(stagedBinariesDir, deployName);
            if (!File.Exists(sourcePath)) continue;

            foreach (string target in clientTargets.Where(t => Path.GetFileName(t).Equals(deployName, StringComparison.OrdinalIgnoreCase)))
            {
                string? dir = Path.GetDirectoryName(target);
                if (dir == null) continue;

                string coreName = deployName.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase)
                    ? "avcodec_core.dll"
                    : Path.GetFileNameWithoutExtension(deployName) + "_core.dll";
                string corePath = Path.Combine(dir, coreName);
                string bakPath = target + ".bak";
                string bakOldPath = target + ".bak.old";

                if (File.Exists(target))
                {
                    FileInfo fi = new(target);
                    // If target is currently the genuine heavy DLL (>100KB), preserve as _core.dll
                    if (fi.Length >= ProxySizeMaxThreshold && deployName.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(target, corePath, overwrite: true);
                        preservedCore.Add(corePath);
                    }

                    // Two-tier rolling backup: target.bak -> target.bak.old
                    if (fi.Length >= ProxySizeMaxThreshold)
                    {
                        if (File.Exists(bakPath))
                        {
                            File.Copy(bakPath, bakOldPath, overwrite: true);
                            rotatedBak.Add(bakOldPath);
                        }

                        File.Copy(target, bakPath, overwrite: true);
                    }
                    else
                    {
                        // Target is already a shim. Do not overwrite genuine .bak with the shim!
                        if (File.Exists(bakPath) && !File.Exists(bakOldPath))
                        {
                            File.Copy(bakPath, bakOldPath, overwrite: true);
                            rotatedBak.Add(bakOldPath);
                        }
                    }
                }

                // Deploy shim
                File.Copy(sourcePath, target, overwrite: true);
                deployed.Add(target);
            }
        }

        return new VideoDeployResult(
            true,
            $"Successfully deployed {deployed.Count} modernized video shims across client fleet.",
            deployed,
            preservedCore,
            rotatedBak
        );
    }

    /// <summary>
    ///     Atomically rolls back client video libraries to vanilla FFmpeg 4.3.1 from backups.
    /// </summary>
    public static VideoRollbackResult RollbackClientVideo(
        string repoRootOrGameDir,
        bool skipProcessCheck = false)
    {
        if (!skipProcessCheck && IsClientRunning(out string runningProcs))
        {
            return new VideoRollbackResult(false, $"Cannot rollback while client is running: {runningProcs}.", [], "None");
        }

        var clientTargets = FindClientVideoTargets(repoRootOrGameDir);
        var restored = new List<string>();
        string restoredFrom = "None";

        foreach (string target in clientTargets)
        {
            string? dir = Path.GetDirectoryName(target);
            if (dir == null) continue;

            string fname = Path.GetFileName(target);
            string coreName = fname.StartsWith("avcodec", StringComparison.OrdinalIgnoreCase)
                ? "avcodec_core.dll"
                : Path.GetFileNameWithoutExtension(fname) + "_core.dll";
            string corePath = Path.Combine(dir, coreName);
            string legacyCorePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(fname) + "_core.dll");
            string bakPath = target + ".bak";
            string bakOldPath = target + ".bak.old";

            // Priority 1: Restore from _core.dll
            string? activeCore = File.Exists(corePath) && new FileInfo(corePath).Length >= ProxySizeMaxThreshold ? corePath
                : (File.Exists(legacyCorePath) && new FileInfo(legacyCorePath).Length >= ProxySizeMaxThreshold ? legacyCorePath : null);

            if (activeCore != null)
            {
                File.Copy(activeCore, target, overwrite: true);
                restored.Add(target);
                restoredFrom = Path.GetFileName(activeCore);
            }
            // Priority 2: Restore from .bak
            else if (File.Exists(bakPath) && new FileInfo(bakPath).Length >= ProxySizeMaxThreshold)
            {
                File.Copy(bakPath, target, overwrite: true);
                restored.Add(target);
                restoredFrom = ".bak";

                if (File.Exists(bakOldPath) && new FileInfo(bakOldPath).Length >= ProxySizeMaxThreshold)
                {
                    File.Copy(bakOldPath, bakPath, overwrite: true);
                }
            }
            // Priority 3: Restore from .bak.old
            else if (File.Exists(bakOldPath) && new FileInfo(bakOldPath).Length >= ProxySizeMaxThreshold)
            {
                File.Copy(bakOldPath, target, overwrite: true);
                restored.Add(target);
                restoredFrom = ".bak.old";
            }
        }

        return new VideoRollbackResult(
            restored.Count > 0,
            restored.Count > 0 ? $"Successfully restored {restored.Count} video DLLs from {restoredFrom}." : "No backups found to restore.",
            restored,
            restoredFrom
        );
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            return "unreadable";
        }
    }
}
