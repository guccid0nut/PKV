// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FastVfsModernizationService.cs
// System: Protocol Core - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 7 Zstandard (Zstd) VFS Decompression & libdeflate Acceleration
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kongor.Core.Diagnostics;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents the performance benchmark results of decompressing an .s2z game archive.
/// </summary>
public sealed record ArchiveBenchmarkResult(
    bool Success,
    string ArchivePath,
    int TotalFiles,
    long TotalCompressedBytes,
    long TotalUncompressedBytes,
    double ElapsedMilliseconds,
    double ThroughputMbPerSecond,
    double CompressionRatio,
    string? ErrorMessage = null
);

/// <summary>
///     Format inspection details of an .s2z game archive.
/// </summary>
public sealed record ArchiveInspectionReport(
    bool Success,
    string ArchivePath,
    long FileSizeBytes,
    int TotalEntries,
    int DeflateCount,
    int ZstdCount,
    int StoredCount,
    long TotalUncompressedSize,
    string? ErrorMessage = null
);

/// <summary>
///     Status report of active zlibwapi_x64.dll binaries across client and server fleets.
/// </summary>
public sealed record VfsStatusReport(
    string RepoRoot,
    IReadOnlyList<VfsBinaryTarget> Targets,
    bool GameProcessesRunning,
    string RunningProcesses
);

/// <summary>
///     Details of a single zlibwapi_x64.dll target.
/// </summary>
public sealed record VfsBinaryTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string Sha256Hash,
    bool IsDualEngineAccelerated,
    bool HasBak,
    bool HasBakOld
);

/// <summary>
///     Result of an archive binary upgrade or rollback operation.
/// </summary>
public sealed record VfsOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> ModifiedPaths,
    IReadOnlyList<string> RotatedBakOldPaths
);

/// <summary>
///     High-performance service governing Phase 7 Fast VFS modernization, archive benchmarking,
///     format inspection, and atomic two-tier deployment of the dual-engine zlibwapi_x64.dll.
/// </summary>
public static class FastVfsModernizationService
{
    private static readonly string[] RelativeTargetPaths =
    [
        Path.Combine("games", "client", "zlibwapi_x64.dll"),
        Path.Combine("games", "client", "windows-x64", "zlibwapi_x64.dll"),
        Path.Combine("games", "server", "zlibwapi_x64.dll"),
        Path.Combine("games", "server", "windows-x64", "zlibwapi_x64.dll")
    ];

    /// <summary>
    ///     Inspects compression formats and statistics inside an .s2z or .zip game archive.
    /// </summary>
    public static ArchiveInspectionReport InspectArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return new ArchiveInspectionReport(false, archivePath ?? "", 0, 0, 0, 0, 0, 0, $"Archive not found: {archivePath}");
        }

        try
        {
            FileInfo fi = new FileInfo(archivePath);
            int deflateCount = 0;
            int zstdCount = 0;
            int storedCount = 0;
            long totalUncompressed = 0;
            int totalEntries = 0;

            using (ZipArchive zip = ZipFile.OpenRead(archivePath))
            {
                totalEntries = zip.Entries.Count;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    totalUncompressed += entry.Length;

                    if (entry.CompressedLength == entry.Length)
                    {
                        storedCount++;
                    }
                    else if (entry.Name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
                    {
                        zstdCount++;
                    }
                    else
                    {
                        deflateCount++;
                    }
                }
            }

            return new ArchiveInspectionReport(
                true,
                archivePath,
                fi.Length,
                totalEntries,
                deflateCount,
                zstdCount,
                storedCount,
                totalUncompressed
            );
        }
        catch (Exception ex)
        {
            return new ArchiveInspectionReport(false, archivePath, 0, 0, 0, 0, 0, 0, $"Failed inspecting archive: {ex.Message}");
        }
    }

    /// <summary>
    ///     Benchmarks decompression throughput (MB/s) of all entries in an .s2z archive.
    ///     Enforces a per-entry bounded decompression ceiling (#154) to prevent zip-bomb OOMs.
    /// </summary>
    public static ArchiveBenchmarkResult BenchmarkArchive(string archivePath, int maxEntries = 100, int maxEntryBytes = BoundedDecompressionPaginator.DefaultMaxDecompressedBytes)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return new ArchiveBenchmarkResult(false, archivePath ?? "", 0, 0, 0, 0, 0, 0, $"Archive not found: {archivePath}");
        }

        try
        {
            long totalCompressed = 0;
            long totalUncompressed = 0;
            int fileCount = 0;
            byte[] buffer = new byte[65536];

            Stopwatch sw = Stopwatch.StartNew();

            using (ZipArchive zip = ZipFile.OpenRead(archivePath))
            {
                List<ZipArchiveEntry> entries = zip.Entries.Take(maxEntries > 0 ? maxEntries : int.MaxValue).ToList();
                foreach (ZipArchiveEntry entry in entries)
                {
                    totalCompressed += entry.CompressedLength;
                    fileCount++;

                    using Stream stream = entry.Open();
                    long entryRead = 0;
                    while (entryRead < maxEntryBytes)
                    {
                        int toRead = (int)Math.Min(buffer.Length, maxEntryBytes - entryRead);
                        int n = stream.Read(buffer, 0, toRead);
                        if (n <= 0)
                        {
                            break;
                        }
                        entryRead += n;
                    }
                    totalUncompressed += entryRead;
                }
            }

            sw.Stop();
            double elapsedMs = sw.Elapsed.TotalMilliseconds;
            double seconds = elapsedMs / 1000.0;
            double mb = totalUncompressed / (1024.0 * 1024.0);
            double mbPerSec = seconds > 0.0001 ? mb / seconds : 0.0;
            double ratio = totalUncompressed > 0 ? (double)totalCompressed / totalUncompressed : 1.0;

            return new ArchiveBenchmarkResult(
                true,
                archivePath,
                fileCount,
                totalCompressed,
                totalUncompressed,
                elapsedMs,
                Math.Round(mbPerSec, 2),
                Math.Round(ratio, 4)
            );
        }
        catch (Exception ex)
        {
            return new ArchiveBenchmarkResult(false, archivePath, 0, 0, 0, 0, 0, 0, $"Benchmark failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets the current status of zlibwapi_x64.dll across all targets in the specified fleet.
    /// </summary>
    public static VfsStatusReport GetVfsStatus(string repoRootOrGameDir, TargetFleet fleet = TargetFleet.All)
    {
        string repoRoot = ResolveRepoRoot(repoRootOrGameDir);
        IReadOnlyList<string> targetPaths = FindVfsTargets(repoRoot, fleet);
        List<VfsBinaryTarget> results = new List<VfsBinaryTarget>(targetPaths.Count);

        foreach (string target in targetPaths)
        {
            string rel = Path.GetRelativePath(repoRoot, target);
            bool exists = File.Exists(target);
            bool hasBak = File.Exists(target + ".bak");
            bool hasBakOld = File.Exists(target + ".bak.old");
            long size = 0;
            string sha = "";
            bool isDualEngine = false;

            if (exists)
            {
                try
                {
                    FileInfo fi = new FileInfo(target);
                    size = fi.Length;
                    byte[] bytes = File.ReadAllBytes(target);
                    sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    // Our dual-engine build with static Zstd decoder is >= 180 KB
                    isDualEngine = size > 180_000;
                }
                catch
                {
                    // Unreadable target
                }
            }

            results.Add(new VfsBinaryTarget(rel, target, exists, size, sha, isDualEngine, hasBak, hasBakOld));
        }

        bool procsRunning = NativeBinaryManager.IsGameOrServerRunning(out string runningProcs);
        return new VfsStatusReport(repoRoot, results, procsRunning, runningProcs);
    }

    /// <summary>
    ///     Safely deploys the modern dual-engine zlibwapi_x64.dll to targets in the specified fleet.
    ///     Rotates existing .bak -> .bak.old and creates new .bak before overwriting.
    /// </summary>
    public static VfsOperationResult DeployFastVfsBinary(
        string repoRootOrGameDir,
        string? candidatePath = null,
        bool skipProcessCheck = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            return new VfsOperationResult(
                false,
                $"Active game or server processes detected ({runningProcesses}). Close hon_x64 and COMPEL before deploying or pass skipProcessCheck=true.",
                [], []);
        }

        string repoRoot = ResolveRepoRoot(repoRootOrGameDir);
        string effectiveCandidate = candidatePath ?? (
            File.Exists(Path.Combine(repoRoot, "native", "build", "kongor_zlib_build", "Release", "zlibwapi_x64.dll"))
                ? Path.Combine(repoRoot, "native", "build", "kongor_zlib_build", "Release", "zlibwapi_x64.dll")
                : Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "zlibwapi_x64.dll"));

        if (!File.Exists(effectiveCandidate))
        {
            return new VfsOperationResult(false, $"Staged candidate zlibwapi_x64.dll not found at '{effectiveCandidate}'.", [], []);
        }

        // Verify C-ABI before deploying
        ZlibVerificationResult verification = NativeZlibBridge.VerifyLibrary(effectiveCandidate);
        if (!verification.Success)
        {
            return new VfsOperationResult(false, $"Candidate binary failed C-ABI verification: {verification.ErrorMessage}", [], []);
        }

        IReadOnlyList<string> targets = FindVfsTargets(repoRoot, fleet);
        List<string> modified = new List<string>();
        List<string> rotatedBakOld = new List<string>();

        foreach (string targetPath in targets)
        {
            try
            {
                string dir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string bakPath = targetPath + ".bak";
                string bakOldPath = targetPath + ".bak.old";

                if (File.Exists(targetPath))
                {
                    // Rotate .bak -> .bak.old
                    if (File.Exists(bakPath))
                    {
                        File.Copy(bakPath, bakOldPath, true);
                        if (!rotatedBakOld.Contains(bakOldPath)) rotatedBakOld.Add(bakOldPath);
                        logAction?.Invoke($"[FAST_VFS] Rotated '{bakPath}' -> '{bakOldPath}'.");
                    }

                    // Preserve active -> .bak
                    File.Copy(targetPath, bakPath, true);
                    logAction?.Invoke($"[FAST_VFS] Preserved active '{targetPath}' -> '{bakPath}'.");
                }

                File.Copy(effectiveCandidate, targetPath, true);
                modified.Add(targetPath);
                logAction?.Invoke($"[FAST_VFS] Deployed dual-engine zlibwapi_x64.dll -> '{targetPath}'.");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[FAST_VFS] Failed deploying to '{targetPath}': {ex.Message}");
            }
        }

        string msg = $"Successfully deployed dual-engine zlibwapi_x64.dll to {modified.Count} targets ({fleet}).";
        return new VfsOperationResult(modified.Count > 0, msg, modified, rotatedBakOld);
    }

    /// <summary>
    ///     Rolls back zlibwapi_x64.dll to .bak (previous tier) or .bak.old (historical baseline tier).
    /// </summary>
    public static VfsOperationResult RollbackFastVfsBinary(
        string repoRootOrGameDir,
        string tier = "previous",
        bool skipProcessCheck = false,
        TargetFleet fleet = TargetFleet.All,
        Action<string>? logAction = null)
    {
        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcesses))
        {
            return new VfsOperationResult(
                false,
                $"Active game or server processes detected ({runningProcesses}). Close hon_x64 and COMPEL before rollback or pass skipProcessCheck=true.",
                [], []);
        }

        string repoRoot = ResolveRepoRoot(repoRootOrGameDir);
        IReadOnlyList<string> targets = FindVfsTargets(repoRoot, fleet);
        List<string> rolledBack = new List<string>();

        bool useOld = tier.Equals("old", StringComparison.OrdinalIgnoreCase) ||
                      tier.Equals("baseline", StringComparison.OrdinalIgnoreCase) ||
                      tier.Equals("bak.old", StringComparison.OrdinalIgnoreCase);

        foreach (string targetPath in targets)
        {
            string backupToRestore = useOld ? targetPath + ".bak.old" : targetPath + ".bak";
            if (!File.Exists(backupToRestore))
            {
                logAction?.Invoke($"[FAST_VFS] Backup tier '{tier}' not found for '{targetPath}', skipping.");
                continue;
            }

            try
            {
                File.Copy(backupToRestore, targetPath, true);
                rolledBack.Add(targetPath);
                logAction?.Invoke($"[FAST_VFS] Restored '{targetPath}' from '{backupToRestore}'.");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"[FAST_VFS] Failed rolling back '{targetPath}': {ex.Message}");
            }
        }

        string msg = $"Successfully rolled back {rolledBack.Count} targets ({fleet}) to tier '{tier}'.";
        return new VfsOperationResult(rolledBack.Count > 0, msg, rolledBack, []);
    }

    public static IReadOnlyList<string> FindVfsTargets(string repoRoot, TargetFleet fleet = TargetFleet.All)
    {
        List<string> targets = new List<string>();

        foreach (string rel in RelativeTargetPaths)
        {
            bool isClient = rel.Contains(Path.Combine("games", "client"), StringComparison.OrdinalIgnoreCase);
            bool isServer = rel.Contains(Path.Combine("games", "server"), StringComparison.OrdinalIgnoreCase);

            if (fleet == TargetFleet.Client && !isClient) continue;
            if (fleet == TargetFleet.Server && !isServer) continue;

            string full = Path.GetFullPath(Path.Combine(repoRoot, rel));
            targets.Add(full);
        }

        return targets;
    }

    private static string ResolveRepoRoot(string path)
    {
        DirectoryInfo? dir = new DirectoryInfo(path);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.sln")) ||
                File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Path.GetFullPath(path);
    }
}
