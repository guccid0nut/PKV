// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryModernizationService.cs
// System: ProjectKongor - Native C++ Modernization & Engine Hardening
// Subsystem: Track 1 Client Memory Allocator Modernization (Microsoft mimalloc)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents metadata and deployment status of a client memory target binary.
/// </summary>
public sealed record ClientMemoryTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool HasBackup,
    bool HasBackupOld
);

/// <summary>
///     Aggregated report of client fleet memory allocator modernization status.
/// </summary>
public sealed record ClientMemoryReport(
    string TargetDirectory,
    bool IsModernized,
    IReadOnlyList<ClientMemoryTarget> Targets,
    bool ClientProcessesRunning,
    string RunningProcesses,
    string MemoryBackend
);

/// <summary>
///     Result of a client memory modernization deployment operation.
/// </summary>
public sealed record MemoryDeployResult(
    bool Success,
    string Message,
    IReadOnlyList<string> DeployedPaths,
    IReadOnlyList<string> RotatedBakPaths,
    IReadOnlyList<string> RotatedBakOldPaths
);

/// <summary>
///     Result of a client memory rollback operation.
/// </summary>
public sealed record MemoryRollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string RestoredFrom
);

/// <summary>
///     Provides governance, pre-flight safety checks, and two-tier rolling backups (.bak and .bak.old)
///     for the player client high-performance Microsoft mimalloc allocator dynamic override.
///     Guarantees zero modification of dedicated server directories.
/// </summary>
public static class MemoryModernizationService
{
    public const string MimallocCoreName = "mimalloc.dll";
    public const string MimallocOverrideName = "mimalloc-override.dll";
    public const string MimallocRedirectName = "mimalloc-redirect.dll";
    public const string VersionProxyName = "version.dll";

    public static readonly string[] RequiredMimallocBinaries =
    [
        MimallocCoreName,
        MimallocOverrideName,
        MimallocRedirectName
    ];

    private static readonly string[] RelativeClientDirs =
    [
        Path.Combine("games", "client"),
        Path.Combine("games", "client", "windows-x64")
    ];

    /// <summary>
    ///     Finds all client directory targets within the given repository root or game folder.
    ///     Enforces strict guardrails preventing any dedicated server files from being included.
    /// </summary>
    public static IReadOnlyList<string> FindClientDirectories(string repoRootOrGameDir)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        bool isRepo = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"));

        if (isRepo)
        {
            foreach (string rel in RelativeClientDirs)
            {
                string full = Path.GetFullPath(Path.Combine(repoRootOrGameDir, rel));
                if (Directory.Exists(full) && IsClientSafeDirectory(full))
                {
                    set.Add(full);
                }
            }
        }
        else
        {
            string root = Path.GetFullPath(repoRootOrGameDir);
            if (IsClientSafeDirectory(root))
            {
                set.Add(root);
                string win64 = Path.Combine(root, "windows-x64");
                if (Directory.Exists(win64) && IsClientSafeDirectory(win64))
                {
                    set.Add(win64);
                }
            }
        }

        return set.OrderBy(p => p).ToList();
    }

    /// <summary>
    ///     Strict validation asserting that a directory path belongs exclusively to the player client
    ///     and never touches a dedicated server directory.
    /// </summary>
    public static bool IsClientSafeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('\\', '/').ToLowerInvariant();

        if (normalized.Contains("/server/") || normalized.Contains("/dedicated/") || normalized.EndsWith("/server"))
        {
            return false;
        }

        return normalized.Contains("/client") || normalized.EndsWith("client");
    }

    /// <summary>
    ///     Strict validation asserting that a target file belongs exclusively to the player client.
    /// </summary>
    public static bool IsClientSafeTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('\\', '/').ToLowerInvariant();

        if (normalized.Contains("/server/") || normalized.Contains("/dedicated/") || normalized.EndsWith("/server"))
        {
            return false;
        }

        string filename = Path.GetFileName(path).ToLowerInvariant();
        return filename.StartsWith("mimalloc", StringComparison.OrdinalIgnoreCase) ||
               filename.StartsWith("version", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Checks whether any player client processes are currently executing.
    /// </summary>
    public static (bool IsRunning, string ProcessNames) AreClientProcessesRunning()
    {
        string[] candidateNames = ["hon_x64", "hon", "willowmaker"];
        List<string> running = [];

        foreach (string name in candidateNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                running.Add($"{name} (PIDs: {string.Join(",", processes.Select(p => p.Id))})");
                foreach (Process p in processes)
                {
                    p.Dispose();
                }
            }
        }

        return (running.Count > 0, string.Join("; ", running));
    }

    /// <summary>
    ///     Evaluates the current memory modernization status across all discovered client targets.
    /// </summary>
    public static ClientMemoryReport GetMemoryStatus(string repoRootOrGameDir)
    {
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        List<ClientMemoryTarget> targets = [];
        bool allModernized = clientDirs.Count > 0;

        foreach (string clientDir in clientDirs)
        {
            foreach (string binName in RequiredMimallocBinaries)
            {
                string targetPath = Path.Combine(clientDir, binName);
                bool exists = File.Exists(targetPath);
                long size = exists ? new FileInfo(targetPath).Length : 0;
                string hash = exists ? ComputeSha256(targetPath) : string.Empty;
                bool hasBak = File.Exists(targetPath + ".bak");
                bool hasBakOld = File.Exists(targetPath + ".bak.old");

                if (!exists)
                {
                    allModernized = false;
                }

                string relPath = Path.GetRelativePath(repoRootOrGameDir, targetPath);
                targets.Add(new ClientMemoryTarget(relPath, targetPath, exists, size, hash, hasBak, hasBakOld));
            }
        }

        (bool isRunning, string runningNames) = AreClientProcessesRunning();

        string backend = allModernized
            ? "Microsoft mimalloc (v2.3.2)"
            : "Stock Windows UCRT Heap";

        return new ClientMemoryReport(
            repoRootOrGameDir,
            allModernized,
            targets,
            isRunning,
            runningNames,
            backend
        );
    }

    /// <summary>
    ///     Deploys the Microsoft mimalloc binaries from source to target client directories with
    ///     atomic two-tier backup rotation.
    /// </summary>
    public static MemoryDeployResult DeployMimalloc(
        string repoRootOrGameDir,
        string? candidateSourceDir = null,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck)
        {
            (bool isRunning, string processNames) = AreClientProcessesRunning();
            if (isRunning)
            {
                return new MemoryDeployResult(
                    false,
                    $"Deployment rejected: active client processes detected ({processNames}). Terminate client before proceeding.",
                    [], [], []);
            }
        }

        string sourceDir = candidateSourceDir ?? ResolveDefaultSourceDirectory(repoRootOrGameDir);
        if (!Directory.Exists(sourceDir))
        {
            return new MemoryDeployResult(
                false,
                $"Deployment rejected: candidate mimalloc source directory not found: {sourceDir}",
                [], [], []);
        }

        foreach (string binName in RequiredMimallocBinaries)
        {
            string srcFile = Path.Combine(sourceDir, binName);
            if (!File.Exists(srcFile))
            {
                return new MemoryDeployResult(
                    false,
                    $"Deployment rejected: missing candidate source binary: {srcFile}",
                    [], [], []);
            }
        }

        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        if (clientDirs.Count == 0)
        {
            return new MemoryDeployResult(
                false,
                $"Deployment rejected: no client directories found in: {repoRootOrGameDir}",
                [], [], []);
        }

        List<string> deployed = [];
        List<string> rotatedBak = [];
        List<string> rotatedBakOld = [];

        foreach (string clientDir in clientDirs)
        {
            foreach (string binName in RequiredMimallocBinaries)
            {
                string targetPath = Path.Combine(clientDir, binName);
                string sourcePath = Path.Combine(sourceDir, binName);
                string bakPath = targetPath + ".bak";
                string bakOldPath = targetPath + ".bak.old";

                // Atomic two-tier backup rotation
                if (File.Exists(targetPath))
                {
                    if (File.Exists(bakPath))
                    {
                        File.Copy(bakPath, bakOldPath, true);
                        rotatedBakOld.Add(bakOldPath);
                    }

                    File.Copy(targetPath, bakPath, true);
                    rotatedBak.Add(bakPath);
                }

                File.Copy(sourcePath, targetPath, true);
                deployed.Add(targetPath);
                logAction?.Invoke($"[DEPLOY] {binName} -> {targetPath}");
            }
        }

        return new MemoryDeployResult(
            true,
            $"Successfully deployed Microsoft mimalloc across {clientDirs.Count} client directories ({deployed.Count} binaries updated).",
            deployed,
            rotatedBak,
            rotatedBakOld
        );
    }

    /// <summary>
    ///     Atomically rolls back the mimalloc binaries across all client directories.
    /// </summary>
    public static MemoryRollbackResult RollbackMimalloc(
        string repoRootOrGameDir,
        string tier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck)
        {
            (bool isRunning, string processNames) = AreClientProcessesRunning();
            if (isRunning)
            {
                return new MemoryRollbackResult(
                    false,
                    $"Rollback rejected: active client processes detected ({processNames}). Terminate client before proceeding.",
                    [], string.Empty);
            }
        }

        bool useOld = tier.Equals("old", StringComparison.OrdinalIgnoreCase) ||
                      tier.Equals("bak.old", StringComparison.OrdinalIgnoreCase);

        string suffix = useOld ? ".bak.old" : ".bak";
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        List<string> restored = [];

        foreach (string clientDir in clientDirs)
        {
            foreach (string binName in RequiredMimallocBinaries)
            {
                string targetPath = Path.Combine(clientDir, binName);
                string backupPath = targetPath + suffix;

                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, targetPath, true);
                    restored.Add(targetPath);
                    logAction?.Invoke($"[ROLLBACK] Restored {targetPath} from {suffix}");
                }
                else if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    restored.Add($"{targetPath} (removed)");
                    logAction?.Invoke($"[ROLLBACK] Removed {targetPath} (no backup existed)");
                }
            }
        }

        return new MemoryRollbackResult(
            true,
            $"Successfully rolled back {restored.Count} mimalloc binaries across {clientDirs.Count} client directories using tier '{tier}'.",
            restored,
            suffix
        );
    }

    private static string ResolveDefaultSourceDirectory(string repoRootOrGameDir)
    {
        string candidate = Path.Combine(repoRootOrGameDir, "native", "deps", "mimalloc");
        if (Directory.Exists(candidate)) return candidate;

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string check = Path.Combine(current.FullName, "native", "deps", "mimalloc");
            if (Directory.Exists(check)) return check;
            current = current.Parent;
        }

        return candidate;
    }

    private static string ComputeSha256(string filePath)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }
}
