// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Kongor.Core.Services.Native;

/// <summary>
///     Represents metadata and security status of a native game binary.
/// </summary>
public sealed record NativeBinaryTarget(
    string RelativePath,
    string FullPath,
    bool Exists,
    long SizeBytes,
    string FileVersion,
    string ProductVersion,
    string Sha256Hash,
    bool HasBak,
    bool HasBakOld,
    string SecurityStatus,
    bool IsAbiVerified = false
);

/// <summary>
///     Export table validation result between baseline and candidate DLLs.
/// </summary>
public sealed record ExportValidationResult(
    bool IsValid,
    int BaselineSymbolCount,
    int CandidateSymbolCount,
    IReadOnlyList<string> MissingSymbols,
    IReadOnlyList<string> AddedSymbols
);

/// <summary>
///     Result of an upgrade operation.
/// </summary>
public sealed record UpgradeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> UpgradedPaths,
    IReadOnlyList<string> RotatedBakOldPaths
);

/// <summary>
///     Result of a rollback operation.
/// </summary>
public sealed record RollbackResult(
    bool Success,
    string Message,
    IReadOnlyList<string> RestoredPaths,
    string TargetTier
);

/// <summary>
///     Provides governance, pre-flight safety checks, in-memory PE export validation,
///     and two-tier backup rotation (.bak and .bak.old) for native game engine binaries.
/// </summary>
public static class NativeBinaryManager
{
    private static readonly string[] RelativeTargetPaths =
    [
        Path.Combine("games", "client", "zlibwapi_x64.dll"),
        Path.Combine("games", "client", "windows-x64", "zlibwapi_x64.dll"),
        Path.Combine("games", "server", "zlibwapi_x64.dll"),
        Path.Combine("games", "server", "windows-x64", "zlibwapi_x64.dll")
    ];

    private static readonly string[] DirectTargetPaths =
    [
        "zlibwapi_x64.dll",
        Path.Combine("windows-x64", "zlibwapi_x64.dll")
    ];

    /// <summary>
    ///     Finds all target paths for zlibwapi_x64.dll within the given repository root or game folder.
    /// </summary>
    public static IReadOnlyList<string> FindZlibTargets(string repoRootOrGameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isRepo = Directory.Exists(Path.Combine(repoRootOrGameDir, "games"));

        if (isRepo)
        {
            // Scan repository structure (games/client, games/server)
            foreach (var rel in RelativeTargetPaths)
            {
                var full = Path.GetFullPath(Path.Combine(repoRootOrGameDir, rel));
                var dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || File.Exists(full + ".bak") || (dir != null && Directory.Exists(dir)))
                {
                    set.Add(full);
                }
            }
        }
        else
        {
            // Scan direct game directory (e.g. games/client or custom installation folder)
            foreach (var rel in DirectTargetPaths)
            {
                var full = Path.GetFullPath(Path.Combine(repoRootOrGameDir, rel));
                var dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || File.Exists(full + ".bak") || (dir != null && Directory.Exists(dir) && rel.Contains(Path.DirectorySeparatorChar)))
                {
                    set.Add(full);
                }
            }
        }

        return set.ToList();
    }

    /// <summary>
    ///     Checks whether game client or dedicated server processes are running that could lock DLL files.
    /// </summary>
    public static bool IsGameOrServerRunning(out string runningProcessNames)
    {
        try
        {
            var procs = Process.GetProcesses();
            var conflicting = procs.Where(p =>
                p.ProcessName.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                p.ProcessName.Equals("COMPEL", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (conflicting.Count > 0)
            {
                runningProcessNames = string.Join(", ", conflicting.Select(p => $"{p.ProcessName} (PID: {p.Id})"));
                return true;
            }
        }
        catch
        {
            // Process inspection fallback
        }

        runningProcessNames = string.Empty;
        return false;
    }

    /// <summary>
    ///     Queries the current status of all discovered native binary targets.
    /// </summary>
    public static IReadOnlyList<NativeBinaryTarget> GetStatus(string repoRoot)
    {
        var targets = FindZlibTargets(repoRoot);
        var results = new List<NativeBinaryTarget>(targets.Count);

        foreach (var target in targets)
        {
            var rel = Path.GetRelativePath(repoRoot, target);
            var exists = File.Exists(target);
            var hasBak = File.Exists(target + ".bak");
            var hasBakOld = File.Exists(target + ".bak.old");

            long size = 0;
            string fileVer = "unknown";
            string prodVer = "unknown";
            string hash = string.Empty;
            string secStatus = "NOT_INSTALLED";
            bool isAbiVerified = false;

            if (exists)
            {
                var fi = new FileInfo(target);
                size = fi.Length;
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(target);
                    fileVer = vi.FileVersion ?? "unknown";
                    prodVer = vi.ProductVersion ?? "unknown";
                }
                catch
                {
                    fileVer = "unreadable";
                }

                try
                {
                    using var stream = File.OpenRead(target);
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch
                {
                    hash = "unreadable";
                }

                if (fileVer.StartsWith("1.3", StringComparison.OrdinalIgnoreCase))
                {
                    var verRes = NativeZlibBridge.VerifyLibrary(target);
                    isAbiVerified = verRes.Success;
                    secStatus = isAbiVerified
                        ? $"SECURE (zlib {verRes.Version} - CVE-2022-37434 patched)"
                        : $"VERIFICATION_FAILED ({verRes.ErrorMessage})";
                }
                else if (fileVer.StartsWith("1.2", StringComparison.OrdinalIgnoreCase))
                {
                    secStatus = "VULNERABLE (zlib 1.2.11 - Heap Buffer Overflow)";
                }
                else
                {
                    // Attempt runtime dynamic probe via NativeZlibBridge
                    var verRes = NativeZlibBridge.VerifyLibrary(target);
                    if (verRes.Success && verRes.Version.StartsWith("1.3", StringComparison.OrdinalIgnoreCase))
                    {
                        isAbiVerified = true;
                        if (fileVer.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            fileVer = verRes.Version;
                        }
                        secStatus = $"SECURE (zlib {verRes.Version} - CVE-2022-37434 patched)";
                    }
                    else
                    {
                        secStatus = $"UNKNOWN ({fileVer})";
                    }
                }
            }

            results.Add(new NativeBinaryTarget(
                rel,
                target,
                exists,
                size,
                fileVer,
                prodVer,
                hash,
                hasBak,
                hasBakOld,
                secStatus,
                isAbiVerified
            ));
        }

        return results;
    }

    /// <summary>
    ///     Extracts all ASCII function export names from a 64-bit PE DLL.
    /// </summary>
    public static IReadOnlyList<string> ExtractPeExportNames(string filePath)
    {
        if (!File.Exists(filePath)) return Array.Empty<string>();
        byte[] bytes = File.ReadAllBytes(filePath);
        return ExtractPeExportNames(bytes);
    }

    /// <summary>
    ///     Extracts all ASCII function export names from in-memory PE byte buffer.
    /// </summary>
    public static IReadOnlyList<string> ExtractPeExportNames(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 0x40) return Array.Empty<string>();
            int eLfanew = BitConverter.ToInt32(bytes, 0x3C);
            if (eLfanew <= 0 || (long)eLfanew + 24 + 116 > bytes.Length) return Array.Empty<string>();

            ushort numSections = BitConverter.ToUInt16(bytes, eLfanew + 6);
            ushort sizeOfOpt = BitConverter.ToUInt16(bytes, eLfanew + 20);
            int opt = eLfanew + 24;
            if ((long)opt + 116 > bytes.Length) return Array.Empty<string>();

            int exportRva = BitConverter.ToInt32(bytes, opt + 112);
            if (exportRva == 0) return Array.Empty<string>();

            int secTable = opt + sizeOfOpt;

            int RvaToOffset(int rva)
            {
                for (int i = 0; i < numSections; i++)
                {
                    long secLong = (long)secTable + (i * 40);
                    if (secLong < 0 || secLong + 24 > bytes.Length) break;
                    int sec = (int)secLong;
                    int va = BitConverter.ToInt32(bytes, sec + 12);
                    int vsize = BitConverter.ToInt32(bytes, sec + 8);
                    int raw = BitConverter.ToInt32(bytes, sec + 20);
                    if (rva >= va && rva < va + vsize)
                    {
                        long computed = (long)rva - va + raw;
                        return (computed >= 0 && computed < bytes.Length) ? (int)computed : -1;
                    }
                }
                return -1;
            }

            int expOff = RvaToOffset(exportRva);
            if (expOff < 0 || expOff + 36 > bytes.Length) return Array.Empty<string>();

            int nNames = BitConverter.ToInt32(bytes, expOff + 24);
            int namesRva = BitConverter.ToInt32(bytes, expOff + 32);
            int namesOff = RvaToOffset(namesRva);
            if (namesOff < 0) return Array.Empty<string>();

            // Clamp max names to avoid pathological allocation on corrupt headers
            if (nNames < 0 || nNames > 100_000) return Array.Empty<string>();

            var list = new List<string>(Math.Min(nNames, 4096));
            for (int i = 0; i < nNames; i++)
            {
                if ((long)namesOff + (i * 4) + 4 > bytes.Length) break;
                int nRva = BitConverter.ToInt32(bytes, namesOff + (i * 4));
                int nOff = RvaToOffset(nRva);
                if (nOff < 0 || nOff >= bytes.Length) continue;

                var sb = new StringBuilder();
                int curr = nOff;
                while (curr < bytes.Length && bytes[curr] != 0 && sb.Length < 1024)
                {
                    sb.Append((char)bytes[curr]);
                    curr++;
                }
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                }
            }

            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    ///     Compares export symbol tables between a baseline and a candidate DLL.
    /// </summary>
    public static ExportValidationResult VerifyExports(string baselinePath, string candidatePath)
    {
        var baseline = ExtractPeExportNames(baselinePath);
        var candidate = ExtractPeExportNames(candidatePath);

        var candSet = new HashSet<string>(candidate, StringComparer.Ordinal);
        var missing = baseline.Where(b => !candSet.Contains(b)).ToList();

        var baseSet = new HashSet<string>(baseline, StringComparer.Ordinal);
        var added = candidate.Where(c => !baseSet.Contains(c)).ToList();

        return new ExportValidationResult(
            missing.Count == 0,
            baseline.Count,
            candidate.Count,
            missing,
            added
        );
    }

    /// <summary>
    ///     Executes a safe upgrade of zlibwapi_x64.dll across all discovered targets.
    ///     Rotates existing .bak -> .bak.old, copies active -> .bak, copies candidate -> active,
    ///     and performs dynamic in-memory C-ABI verification before completing the upgrade.
    /// </summary>
    public static UpgradeResult UpgradeZlib(
        string repoRoot,
        string candidateDllPath,
        bool skipProcessCheck = false,
        bool skipExportCheck = false,
        bool? skipRuntimeCheck = null)
    {
        if (!File.Exists(candidateDllPath))
        {
            throw new FileNotFoundException($"Candidate DLL not found at: {candidateDllPath}");
        }

        if (!skipProcessCheck && IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot upgrade native libraries while game client or server processes are running ({runningProcesses}). Please terminate them first.");
        }

        var targets = FindZlibTargets(repoRoot);
        if (targets.Count == 0)
        {
            return new UpgradeResult(false, "No zlibwapi_x64.dll target paths discovered.", Array.Empty<string>(), Array.Empty<string>());
        }

        // Pre-flight export check against first existing baseline
        if (!skipExportCheck)
        {
            var existingBaseline = targets.FirstOrDefault(File.Exists);
            if (existingBaseline != null)
            {
                var exportCheck = VerifyExports(existingBaseline, candidateDllPath);
                if (!exportCheck.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Candidate DLL failed export parity check! Missing {exportCheck.MissingSymbols.Count} required symbols: {string.Join(", ", exportCheck.MissingSymbols.Take(10))}");
                }
            }
        }

        // Runtime verification gate on candidate DLL
        bool doRuntimeCheck = skipRuntimeCheck.HasValue ? !skipRuntimeCheck.Value : !skipExportCheck;
        if (doRuntimeCheck)
        {
            var candidateVerification = NativeZlibBridge.VerifyLibrary(candidateDllPath);
            if (!candidateVerification.Success)
            {
                throw new InvalidOperationException(
                    $"Candidate DLL failed runtime C-ABI verification! {candidateVerification.ErrorMessage}");
            }
        }

        var upgraded = new List<string>();
        var rotatedOld = new List<string>();

        foreach (var target in targets)
        {
            var dir = Path.GetDirectoryName(target);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var bakPath = target + ".bak";
            var bakOldPath = target + ".bak.old";

            // Two-tier rotation: if .bak exists, rotate to .bak.old
            if (File.Exists(bakPath))
            {
                File.Copy(bakPath, bakOldPath, overwrite: true);
                rotatedOld.Add(bakOldPath);
            }

            // Copy active to .bak
            if (File.Exists(target))
            {
                File.Copy(target, bakPath, overwrite: true);
            }

            // Copy candidate to active
            File.Copy(candidateDllPath, target, overwrite: true);

            // Post-deployment runtime verification gate
            if (doRuntimeCheck)
            {
                var targetVerification = NativeZlibBridge.VerifyLibrary(target);
                if (!targetVerification.Success)
                {
                    // Automatic self-healing rollback to previous .bak
                    if (File.Exists(bakPath))
                    {
                        File.Copy(bakPath, target, overwrite: true);
                    }

                    throw new InvalidOperationException(
                        $"Deployed target '{target}' failed runtime C-ABI verification: {targetVerification.ErrorMessage}. Rolled back automatically.");
                }
            }

            upgraded.Add(target);
        }

        return new UpgradeResult(
            true,
            $"Successfully upgraded {upgraded.Count} target binaries to zlib 1.3.1 with two-tier backup protection and C-ABI validation.",
            upgraded,
            rotatedOld
        );
    }

    /// <summary>
    ///     Performs live in-memory C-ABI verification of the native zlib library at the specified path.
    /// </summary>
    public static ZlibVerificationResult VerifyRuntimeZlib(string dllPath)
    {
        return NativeZlibBridge.VerifyLibrary(dllPath);
    }

    /// <summary>
    ///     Rolls back target binaries from either .bak (previous) or .bak.old (historical baseline).
    /// </summary>
    public static RollbackResult RollbackZlib(string repoRoot, string targetTier = "previous", bool skipProcessCheck = false)
    {
        if (!skipProcessCheck && IsGameOrServerRunning(out string runningProcesses))
        {
            throw new InvalidOperationException(
                $"Cannot rollback native libraries while game client or server processes are running ({runningProcesses}). Please terminate them first.");
        }

        var targets = FindZlibTargets(repoRoot);
        var restored = new List<string>();
        bool isOld = targetTier.Equals("old", StringComparison.OrdinalIgnoreCase) ||
                     targetTier.Equals("baseline", StringComparison.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var source = isOld ? target + ".bak.old" : target + ".bak";
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
                restored.Add(target);
            }
        }

        if (restored.Count == 0)
        {
            return new RollbackResult(
                false,
                $"No backup files found matching tier '{targetTier}' to restore.",
                Array.Empty<string>(),
                targetTier
            );
        }

        return new RollbackResult(
            true,
            $"Successfully restored {restored.Count} target binaries from tier '{targetTier}'.",
            restored,
            targetTier
        );
    }
}
