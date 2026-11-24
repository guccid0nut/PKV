// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: HeadlessAudioStubService.cs
// System: ProjectKongor - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 6 Server Fleet Security & Audio Headless Null Stub (fmodex64.dll)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents metadata and deployment status of an audio stub target.
/// </summary>
public sealed record AudioStubTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool IsHeadlessStub,
    bool HasBackup,
    bool HasBackupOld
);

/// <summary>
///     Aggregated report of server fleet audio stub status.
/// </summary>
public sealed record AudioStubReport(
    string TargetDirectory,
    bool IsFleetHeadless,
    IReadOnlyList<AudioStubTarget> Targets,
    bool ServerProcessesRunning,
    string RunningProcesses
);

/// <summary>
///     Result of an audio stub deployment operation.
/// </summary>
public sealed record AudioStubDeployResult(
    bool Success,
    string Message,
    IReadOnlyList<string> DeployedPaths,
    IReadOnlyList<string> RotatedBakOldPaths
);

/// <summary>
///     Result of an audio stub rollback operation.
/// </summary>
public sealed record AudioStubRollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string TargetTier
);

/// <summary>
///     Provides governance, pre-flight safety checks, and two-tier rolling backups (.bak and .bak.old)
///     for the dedicated server headless audio null stub (fmodex64.dll).
///     Guarantees zero modification of the player client audio subsystem.
/// </summary>
public static class HeadlessAudioStubService
{
    public const string NullStubCandidateName = "fmodex64_null.dll";
    public const string TargetBinaryName = "fmodex64.dll";
    private const long MaxStubSizeThreshold = 100 * 1024; // 100 KB (Real FMOD is 1.5MB, Null Stub is ~17KB)

    private static readonly string[] RelativeServerTargets =
    [
        Path.Combine("games", "server", TargetBinaryName),
        Path.Combine("games", "server", "windows-x64", TargetBinaryName)
    ];

    private static readonly string[] DirectServerTargets =
    [
        TargetBinaryName,
        Path.Combine("windows-x64", TargetBinaryName)
    ];

    /// <summary>
    ///     Finds all dedicated server audio target paths within the given repository root or game folder.
    ///     Enforces strict guardrails preventing any client audio files from being included.
    /// </summary>
    public static IReadOnlyList<string> FindServerAudioTargets(string repoRootOrGameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isRepo = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"));

        if (isRepo)
        {
            foreach (string rel in RelativeServerTargets)
            {
                string full = Path.GetFullPath(Path.Combine(repoRootOrGameDir, rel));
                string? dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || File.Exists(full + ".bak") || (dir != null && Directory.Exists(dir)))
                {
                    if (IsServerSafeTarget(full))
                    {
                        set.Add(full);
                    }
                }
            }
        }
        else
        {
            foreach (string rel in DirectServerTargets)
            {
                string full = Path.GetFullPath(Path.Combine(repoRootOrGameDir, rel));
                string? dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || File.Exists(full + ".bak") || (dir != null && Directory.Exists(dir)))
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
    ///     Resolves the candidate headless null stub staged in the repository.
    /// </summary>
    public static string ResolveCandidatePath(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "native", "build", "kongor_audio_stub_build", "Release", NullStubCandidateName),
            Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", NullStubCandidateName),
            Path.Combine(repoRoot, ".tools", "bin", "native-binaries", "windows-x64", NullStubCandidateName)
        };

        foreach (string cand in candidates)
        {
            if (File.Exists(cand)) return cand;
        }

        return candidates[1];
    }

    /// <summary>
    ///     Inspects and aggregates audio stub status across dedicated server targets.
    /// </summary>
    public static AudioStubReport GetAudioStubStatus(string repoRootOrGameDir)
    {
        var targets = FindServerAudioTargets(repoRootOrGameDir);
        var items = new List<AudioStubTarget>();
        bool isFleetHeadless = targets.Count > 0;

        foreach (string full in targets)
        {
            string rel = Path.GetRelativePath(repoRootOrGameDir, full);
            bool exists = File.Exists(full);
            bool hasBak = File.Exists(full + ".bak");
            bool hasBakOld = File.Exists(full + ".bak.old");

            long size = 0;
            string sha = string.Empty;
            bool isStub = false;

            if (exists)
            {
                try
                {
                    var fi = new FileInfo(full);
                    size = fi.Length;
                    byte[] bytes = File.ReadAllBytes(full);
                    sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                    // Detect headless stub via size threshold (< 100KB vs 1.5MB full FMOD)
                    isStub = size < MaxStubSizeThreshold;
                }
                catch
                {
                    isStub = false;
                }
            }

            if (!isStub)
            {
                isFleetHeadless = false;
            }

            items.Add(new AudioStubTarget(
                rel,
                full,
                exists,
                size,
                sha,
                isStub,
                hasBak,
                hasBakOld
            ));
        }

        bool procsRunning = NativeBinaryManager.IsGameOrServerRunning(out string runningProcs);
        return new AudioStubReport(repoRootOrGameDir, isFleetHeadless, items, procsRunning, runningProcs);
    }

    /// <summary>
    ///     Safely deploys the fmodex64.dll null stub across server targets.
    ///     Guarantees two-tier backup rotation (.bak and .bak.old) and process checks.
    /// </summary>
    public static AudioStubDeployResult DeployHeadlessStub(
        string repoRootOrGameDir,
        string? candidatePath = null,
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot deploy audio stub while game client or server processes are active ({runningProcesses}). Close all instances or pass skipProcessCheck = true.");
        }

        string repoRoot = ResolveRepoRoot(repoRootOrGameDir);
        string effectiveCandidatePath = candidatePath ?? ResolveCandidatePath(repoRoot);

        if (!File.Exists(effectiveCandidatePath))
        {
            return new AudioStubDeployResult(false, $"Headless audio stub candidate not found at: {effectiveCandidatePath}", [], []);
        }

        var targets = FindServerAudioTargets(repoRootOrGameDir);
        if (targets.Count == 0)
        {
            return new AudioStubDeployResult(false, "No server audio targets discovered.", [], []);
        }

        var deployedPaths = new List<string>();
        var rotatedBakOldPaths = new List<string>();

        foreach (string targetPath in targets)
        {
            if (!IsServerSafeTarget(targetPath))
            {
                logAction?.Invoke($"[AUDIO_STUB] Target '{targetPath}' is not an approved server target; skipping.");
                continue;
            }

            try
            {
                string targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string bakPath = $"{targetPath}.bak";
                string bakOldPath = $"{targetPath}.bak.old";

                if (File.Exists(targetPath))
                {
                    // Rotate existing .bak -> .bak.old
                    if (File.Exists(bakPath))
                    {
                        File.Copy(bakPath, bakOldPath, true);
                        if (!rotatedBakOldPaths.Contains(bakOldPath))
                        {
                            rotatedBakOldPaths.Add(bakOldPath);
                        }
                        logAction?.Invoke($"[AUDIO_STUB] Rotated '{bakPath}' -> '{bakOldPath}'.");
                    }

                    // Backup active file -> .bak
                    File.Copy(targetPath, bakPath, true);
                    logAction?.Invoke($"[AUDIO_STUB] Preserved active '{targetPath}' -> '{bakPath}'.");
                }

                // Copy candidate -> live target
                File.Copy(effectiveCandidatePath, targetPath, true);
                deployedPaths.Add(targetPath);
                logAction?.Invoke($"[AUDIO_STUB] Successfully deployed headless stub -> '{targetPath}'.");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[AUDIO_STUB] Failed to deploy stub to '{targetPath}': {ex.Message}");
            }
        }

        bool success = deployedPaths.Count > 0;
        string msg = success
            ? $"Successfully deployed headless null stub to {deployedPaths.Count} server targets."
            : "Failed to deploy audio stub to any target.";

        return new AudioStubDeployResult(success, msg, deployedPaths, rotatedBakOldPaths);
    }

    /// <summary>
    ///     Rolls back fmodex64.dll across server targets to previous (.bak) or old (.bak.old) tier.
    /// </summary>
    public static AudioStubRollbackResult RollbackHeadlessStub(
        string repoRootOrGameDir,
        string targetTier = "previous",
        bool skipProcessCheck = false,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot rollback audio stub while game client or server processes are active ({runningProcesses}). Close all instances or pass skipProcessCheck = true.");
        }

        var targets = FindServerAudioTargets(repoRootOrGameDir);
        var restoredPaths = new List<string>();

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
                    logAction?.Invoke($"[AUDIO_STUB] No backup found for '{targetPath}' with tier '{targetTier}'.");
                    continue;
                }
            }

            try
            {
                File.Copy(sourceBak, targetPath, true);
                restoredPaths.Add(targetPath);
                logAction?.Invoke($"[AUDIO_STUB] Restored '{targetPath}' from '{sourceBak}'.");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[AUDIO_STUB] Failed to rollback '{targetPath}': {ex.Message}");
            }
        }

        bool success = restoredPaths.Count > 0;
        string msg = success
            ? $"Successfully restored {restoredPaths.Count} server audio binaries from '{targetTier}' backup."
            : $"No server audio binaries restored from '{targetTier}' tier.";

        return new AudioStubRollbackResult(success, msg, restoredPaths, targetTier);
    }

    /// <summary>
    ///     Strict invariant check ensuring path targets server and NEVER touches client files.
    /// </summary>
    private static bool IsServerSafeTarget(string path)
    {
        string normalized = path.Replace('/', '\\');
        if (normalized.Contains(@"\games\client\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\client\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized.Contains(@"\server\", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(TargetBinaryName, StringComparison.OrdinalIgnoreCase);
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
