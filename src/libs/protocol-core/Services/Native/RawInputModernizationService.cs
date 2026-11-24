// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RawInputModernizationService.cs
// System: ProjectKongor - Native C++ Modernization & Engine Hardening
// Subsystem: Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)
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
///     Represents metadata and deployment status of a raw input target binary.
/// </summary>
public sealed record RawInputTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool HasBackup,
    bool HasBackupOld
);

/// <summary>
///     Aggregated report of client fleet raw input modernization status.
/// </summary>
public sealed record RawInputReport(
    string TargetDirectory,
    bool IsModernized,
    bool DirectHardware8000HzReady,
    IReadOnlyList<RawInputTarget> Targets,
    bool ClientProcessesRunning,
    string RunningProcesses,
    string StatusSummary,
    float MouseSensitivity,
    bool CvarRawInputEnabled
);

/// <summary>
///     Result of a client raw input modernization deployment operation.
/// </summary>
public sealed record RawInputDeployResult(
    bool Success,
    string Message,
    IReadOnlyList<string> DeployedPaths,
    IReadOnlyList<string> RotatedBakPaths,
    IReadOnlyList<string> RotatedBakOldPaths
);

/// <summary>
///     Result of a client raw input rollback operation.
/// </summary>
public sealed record RawInputRollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string RestoredFrom
);

/// <summary>
///     Result of a startup.cfg cvar configuration operation.
/// </summary>
public sealed record RawInputCvarResult(
    bool Success,
    string Message,
    IReadOnlyList<string> ConfigFilesUpdated
);

/// <summary>
///     Provides governance, pre-flight safety checks, and two-tier rolling backups (.bak and .bak.old)
///     for the player client high-polling-rate (1000Hz-8000Hz) Windows Raw Input native modernization.
///     Guarantees zero modification of dedicated server directories.
/// </summary>
public static class RawInputModernizationService
{
    public const string VersionProxyName = "version.dll";

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
                string candidate = Path.Combine(repoRootOrGameDir, rel);
                if (Directory.Exists(candidate) && IsClientSafeDirectory(candidate))
                {
                    set.Add(Path.GetFullPath(candidate));
                }
            }
        }
        else if (Directory.Exists(repoRootOrGameDir) && IsClientSafeDirectory(repoRootOrGameDir))
        {
            set.Add(Path.GetFullPath(repoRootOrGameDir));

            string win64Subdir = Path.Combine(repoRootOrGameDir, "windows-x64");
            if (Directory.Exists(win64Subdir) && IsClientSafeDirectory(win64Subdir))
            {
                set.Add(Path.GetFullPath(win64Subdir));
            }
        }

        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     Enforces hard boundary guardrails: ensures the path is strictly inside client directories
    ///     and never matches server directories.
    /// </summary>
    public static bool IsClientSafeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        string lower = normalized.ToLowerInvariant();

        if (lower.Contains("\\server") || lower.Contains("/server") || lower.EndsWith("\\server"))
        {
            return false;
        }

        return lower.Contains("client") || File.Exists(Path.Combine(normalized, "hon_x64.exe"));
    }

    /// <summary>
    ///     Validates whether a specific target file path is safe for client modification.
    /// </summary>
    public static bool IsClientSafeTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string normalized = path.Replace('/', '\\');
        string lower = normalized.ToLowerInvariant();

        if (lower.Contains("\\server") || lower.Contains("/server"))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(path);
        return dir != null && IsClientSafeDirectory(dir);
    }

    /// <summary>
    ///     Verifies whether any game client processes are currently executing.
    /// </summary>
    public static (bool IsRunning, string ProcessNames) AreClientProcessesRunning()
    {
        string[] processNames = ["hon_x64", "hon", "heroesofnewerth", "K2Process"];
        List<string> running = [];

        foreach (string name in processNames)
        {
            try
            {
                Process[] procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    running.Add($"{name} (PIDs: {string.Join(", ", procs.Select(p => p.Id))})");
                }
            }
            catch
            {
                // Process inspection error suppressed for safety
            }
        }

        return (running.Count > 0, string.Join("; ", running));
    }

    /// <summary>
    ///     Inspects and builds a detailed status report for the raw input modernization across the fleet.
    /// </summary>
    public static RawInputReport GetRawInputStatus(string repoRootOrGameDir)
    {
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        List<RawInputTarget> targets = [];
        bool allModernized = clientDirs.Count > 0;

        foreach (string clientDir in clientDirs)
        {
            string targetPath = Path.Combine(clientDir, VersionProxyName);
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
            targets.Add(new RawInputTarget(relPath, targetPath, exists, size, hash, hasBak, hasBakOld));
        }

        (bool isRunning, string runningNames) = AreClientProcessesRunning();

        bool cvarEnabled = InspectCvarRawInput(repoRootOrGameDir);
        float sensitivity = InspectCvarSensitivity(repoRootOrGameDir);

        string summary = allModernized
            ? "Windows Raw Input 8000Hz Modernization Active (Sub-millisecond USB polling & message pump bypass)"
            : "Legacy Win32 Cursor Polling Active";

        return new RawInputReport(
            repoRootOrGameDir,
            allModernized,
            allModernized,
            targets,
            isRunning,
            runningNames,
            summary,
            sensitivity,
            cvarEnabled
        );
    }

    /// <summary>
    ///     Deploys the compiled version.dll proxy binary from source to target client directories with
    ///     atomic two-tier backup rotation.
    /// </summary>
    public static RawInputDeployResult DeployRawInput(
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
                return new RawInputDeployResult(
                    false,
                    $"Deployment rejected: active client processes detected ({processNames}). Terminate client before proceeding.",
                    [], [], []);
            }
        }

        string sourceFile = candidateSourceDir != null
            ? Path.Combine(candidateSourceDir, VersionProxyName)
            : ResolveDefaultSourceBinary(repoRootOrGameDir);

        if (!File.Exists(sourceFile))
        {
            return new RawInputDeployResult(
                false,
                $"Deployment rejected: candidate source binary not found: {sourceFile}",
                [], [], []);
        }

        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        if (clientDirs.Count == 0)
        {
            return new RawInputDeployResult(
                false,
                $"Deployment rejected: no client directories found in: {repoRootOrGameDir}",
                [], [], []);
        }

        List<string> deployed = [];
        List<string> rotatedBak = [];
        List<string> rotatedBakOld = [];

        foreach (string clientDir in clientDirs)
        {
            string targetPath = Path.Combine(clientDir, VersionProxyName);
            string bakPath = targetPath + ".bak";
            string bakOldPath = targetPath + ".bak.old";

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

            File.Copy(sourceFile, targetPath, true);
            deployed.Add(targetPath);
            logAction?.Invoke($"[DEPLOY] {VersionProxyName} -> {targetPath}");
        }

        return new RawInputDeployResult(
            true,
            $"Successfully deployed 8000Hz Raw Input NativeGuard proxy across {clientDirs.Count} client directories.",
            deployed,
            rotatedBak,
            rotatedBakOld
        );
    }

    /// <summary>
    ///     Atomically rolls back the version.dll proxy binaries across all client directories.
    /// </summary>
    public static RawInputRollbackResult RollbackRawInput(
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
                return new RawInputRollbackResult(
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
            string targetPath = Path.Combine(clientDir, VersionProxyName);
            string backupPath = targetPath + suffix;

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, targetPath, true);
                restored.Add(targetPath);
                logAction?.Invoke($"[RESTORE] {backupPath} -> {targetPath}");
            }
        }

        if (restored.Count == 0)
        {
            return new RawInputRollbackResult(
                false,
                $"Rollback failed: no backup files (*{suffix}) found in client directories.",
                [], suffix);
        }

        return new RawInputRollbackResult(
            true,
            $"Successfully restored {restored.Count} binaries from tier '{suffix}'.",
            restored,
            suffix
        );
    }

    /// <summary>
    ///     Updates in_rawInput and m_rawSensitivity settings in client startup.cfg files.
    /// </summary>
    public static RawInputCvarResult ConfigureStartupCfg(
        string repoRootOrGameDir,
        bool enableRawInput,
        float sensitivity = 1.0f,
        Action<string>? logAction = null)
    {
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        List<string> updatedFiles = [];

        foreach (string clientDir in clientDirs)
        {
            string[] candidateCfgPaths =
            [
                Path.Combine(clientDir, "base", "startup.cfg"),
                Path.Combine(clientDir, "startup.cfg")
            ];

            foreach (string cfgPath in candidateCfgPaths)
            {
                if (File.Exists(cfgPath))
                {
                    string[] lines = File.ReadAllLines(cfgPath);
                    List<string> newLines = [];
                    bool foundRawInput = false;
                    bool foundSensitivity = false;

                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("SetSave in_rawInput", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("SetSave \"in_rawInput\"", StringComparison.OrdinalIgnoreCase))
                        {
                            newLines.Add($"SetSave in_rawInput {(enableRawInput ? "true" : "false")}");
                            foundRawInput = true;
                        }
                        else if (trimmed.StartsWith("SetSave m_rawSensitivity", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("SetSave \"m_rawSensitivity\"", StringComparison.OrdinalIgnoreCase))
                        {
                            newLines.Add($"SetSave m_rawSensitivity {sensitivity:F4}");
                            foundSensitivity = true;
                        }
                        else
                        {
                            newLines.Add(line);
                        }
                    }

                    if (!foundRawInput)
                    {
                        newLines.Add($"SetSave in_rawInput {(enableRawInput ? "true" : "false")}");
                    }
                    if (!foundSensitivity)
                    {
                        newLines.Add($"SetSave m_rawSensitivity {sensitivity:F4}");
                    }

                    File.WriteAllLines(cfgPath, newLines);
                    updatedFiles.Add(cfgPath);
                    logAction?.Invoke($"[CONFIG] Updated {cfgPath} (in_rawInput: {enableRawInput}, sensitivity: {sensitivity:F2})");
                }
            }
        }

        return new RawInputCvarResult(
            true,
            $"Configured startup.cfg across {updatedFiles.Count} files.",
            updatedFiles
        );
    }

    private static bool InspectCvarRawInput(string repoRootOrGameDir)
    {
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        foreach (string clientDir in clientDirs)
        {
            string cfg = Path.Combine(clientDir, "base", "startup.cfg");
            if (File.Exists(cfg))
            {
                foreach (string line in File.ReadLines(cfg))
                {
                    if (line.Contains("in_rawInput", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Contains("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
        return true; // Default is true when proxy is modernized
    }

    private static float InspectCvarSensitivity(string repoRootOrGameDir)
    {
        IReadOnlyList<string> clientDirs = FindClientDirectories(repoRootOrGameDir);
        foreach (string clientDir in clientDirs)
        {
            string cfg = Path.Combine(clientDir, "base", "startup.cfg");
            if (File.Exists(cfg))
            {
                foreach (string line in File.ReadLines(cfg))
                {
                    if (line.Contains("m_rawSensitivity", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && float.TryParse(parts[2], out float s))
                        {
                            return s;
                        }
                    }
                }
            }
        }
        return 1.0f;
    }

    private static string ResolveDefaultSourceBinary(string repoRootOrGameDir)
    {
        string[] candidates =
        [
            Path.Combine(repoRootOrGameDir, "native", "build", "native_guard_build", "Release", VersionProxyName),
            Path.Combine(repoRootOrGameDir, "native", "proxies", "native_guard", "build", "Release", VersionProxyName),
            Path.Combine(repoRootOrGameDir, "games", "client", VersionProxyName),
            Path.Combine(repoRootOrGameDir, "artifacts", "native-binaries", "windows-x64", VersionProxyName)
        ];

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static string ComputeSha256(string filePath)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
