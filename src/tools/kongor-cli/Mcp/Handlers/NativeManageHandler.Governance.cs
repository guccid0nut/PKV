// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Native;
using NEXUS.Contracts.Ipc;
using WILLOWMAKER.Core.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class NativeManageHandler
{
    private ToolCallResult HandleStatus(string repoRoot)
    {
        var targets = NativeBinaryManager.GetStatus(repoRoot);
        bool procsRunning = NativeBinaryManager.IsGameOrServerRunning(out string runningProcs);

        var payload = new
        {
            success = true,
            action = "status",
            gameProcessesRunning = procsRunning,
            runningProcesses = runningProcs,
            targetCount = targets.Count,
            targets = targets.Select(t => new
            {
                relativePath = t.RelativePath,
                fullPath = t.FullPath,
                exists = t.Exists,
                sizeBytes = t.SizeBytes,
                fileVersion = t.FileVersion,
                productVersion = t.ProductVersion,
                sha256 = t.Sha256Hash,
                hasBak = t.HasBak,
                hasBakOld = t.HasBakOld,
                securityStatus = t.SecurityStatus
            }).ToList()
        };

        return ToolCallResult.Success(payload);
    }

    private ToolCallResult HandleVerify(string repoRoot, JsonElement arguments)
    {
        string candidate = ResolveCandidate(repoRoot, arguments);
        if (!File.Exists(candidate))
        {
            return ToolCallResult.Error($"Candidate DLL not found at: {candidate}");
        }

        var targets = NativeBinaryManager.FindZlibTargets(repoRoot);
        var baseline = targets.FirstOrDefault(File.Exists);
        if (baseline == null)
        {
            return ToolCallResult.Error("No existing baseline DLL found to compare exports against.");
        }

        var res = NativeBinaryManager.VerifyExports(baseline, candidate);

        var payload = new
        {
            success = res.IsValid,
            action = "verify",
            baselinePath = baseline,
            candidatePath = candidate,
            baselineSymbolCount = res.BaselineSymbolCount,
            candidateSymbolCount = res.CandidateSymbolCount,
            missingSymbolsCount = res.MissingSymbols.Count,
            missingSymbols = res.MissingSymbols,
            addedSymbolsCount = res.AddedSymbols.Count,
            addedSymbols = res.AddedSymbols.Take(10).ToList()
        };

        return res.IsValid ? ToolCallResult.Success(payload) : ToolCallResult.Error("Export parity check failed: missing required symbols.");
    }

    private ToolCallResult HandleUpgrade(string repoRoot, JsonElement arguments)
    {
        string candidate = ResolveCandidate(repoRoot, arguments);
        if (!File.Exists(candidate))
        {
            return ToolCallResult.Error($"Candidate DLL not found at: {candidate}");
        }

        bool skipCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out JsonElement skipElem))
        {
            skipCheck = skipElem.GetBoolean();
        }

        try
        {
            var res = NativeBinaryManager.UpgradeZlib(repoRoot, candidate, skipCheck);
            var payload = new
            {
                success = res.Success,
                action = "upgrade",
                message = res.Message,
                upgradedPaths = res.UpgradedPaths,
                rotatedBakOldPaths = res.RotatedBakOldPaths
            };

            return res.Success ? ToolCallResult.Success(payload) : ToolCallResult.Error(res.Message);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Upgrade error: {ex.Message}");
        }
    }

    private ToolCallResult HandleRollback(string repoRoot, JsonElement arguments)
    {
        string targetTier = "previous";
        if (arguments.TryGetProperty("targetTier", out JsonElement tierElem) && !string.IsNullOrWhiteSpace(tierElem.GetString()))
        {
            targetTier = tierElem.GetString()!.Trim().ToLowerInvariant();
        }

        bool skipCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out JsonElement skipElem))
        {
            skipCheck = skipElem.GetBoolean();
        }

        try
        {
            var res = NativeBinaryManager.RollbackZlib(repoRoot, targetTier, skipCheck);
            var payload = new
            {
                success = res.Success,
                action = "rollback",
                message = res.Message,
                targetTier = res.TargetTier,
                restoredPaths = res.RestoredPaths
            };

            return res.Success ? ToolCallResult.Success(payload) : ToolCallResult.Error(res.Message);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"Rollback error: {ex.Message}");
        }
    }

    private static string ResolveGameDirectory(string repoRoot, JsonElement arguments)
    {
        if (arguments.TryGetProperty("gameDir", out var gd1) && !string.IsNullOrWhiteSpace(gd1.GetString()))
        {
            return Path.GetFullPath(gd1.GetString()!);
        }
        if (arguments.TryGetProperty("game_dir", out var gd2) && !string.IsNullOrWhiteSpace(gd2.GetString()))
        {
            return Path.GetFullPath(gd2.GetString()!);
        }
        if (arguments.TryGetProperty("path", out var pElem) && !string.IsNullOrWhiteSpace(pElem.GetString()) && Directory.Exists(pElem.GetString()!))
        {
            return Path.GetFullPath(pElem.GetString()!);
        }

        string candidate1 = Path.Combine(repoRoot, "games", "client");
        if (Directory.Exists(candidate1)) return candidate1;

        string candidate2 = Path.Combine(repoRoot, "games", "client", "windows-x64");
        if (Directory.Exists(candidate2)) return candidate2;

        return repoRoot;
    }

    private static string ResolveServerDirectory(string repoRoot, JsonElement arguments)
    {
        if (arguments.TryGetProperty("serverDir", out var sd1) && !string.IsNullOrWhiteSpace(sd1.GetString()))
        {
            return Path.GetFullPath(sd1.GetString()!);
        }
        if (arguments.TryGetProperty("server_dir", out var sd2) && !string.IsNullOrWhiteSpace(sd2.GetString()))
        {
            return Path.GetFullPath(sd2.GetString()!);
        }
        if (arguments.TryGetProperty("dir", out var dElem) && !string.IsNullOrWhiteSpace(dElem.GetString()))
        {
            return Path.GetFullPath(dElem.GetString()!);
        }
        if (arguments.TryGetProperty("path", out var pElem) && !string.IsNullOrWhiteSpace(pElem.GetString()))
        {
            return Path.GetFullPath(pElem.GetString()!);
        }

        string candidate1 = Path.Combine(repoRoot, "games", "server");
        if (Directory.Exists(candidate1)) return candidate1;

        return repoRoot;
    }

    private static string ResolveCandidate(string repoRoot, JsonElement arguments)
    {
        if (arguments.TryGetProperty("candidatePath", out JsonElement candElem) && !string.IsNullOrWhiteSpace(candElem.GetString()))
        {
            string p = candElem.GetString()!.Trim();
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        var defaultPaths = new[]
        {
            Path.Combine(repoRoot, "native", "build", "kongor_zlib_build", "Release", "zlibwapi_x64.dll"),
            Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "zlibwapi_x64.dll"),
            Path.Combine(repoRoot, "artifacts", "native-build", "release", "zlibwapi_x64.dll")
        };

        foreach (var p in defaultPaths)
        {
            if (File.Exists(p)) return p;
        }

        return defaultPaths[0];
    }

    private static string ResolveRepoRoot(JsonElement arguments)
    {
        if (arguments.TryGetProperty("repo_root", out var rElem) && !string.IsNullOrWhiteSpace(rElem.GetString()))
        {
            return rElem.GetString()!;
        }
        if (arguments.TryGetProperty("repoRoot", out var rElem2) && !string.IsNullOrWhiteSpace(rElem2.GetString()))
        {
            return rElem2.GetString()!;
        }
        if (arguments.TryGetProperty("path", out var pElem) && !string.IsNullOrWhiteSpace(pElem.GetString()))
        {
            return pElem.GetString()!;
        }

        // Auto-detect repo root by traversing upward from CurrentDirectory / BaseDirectory
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "client", "windows-x64")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "games", "client", "windows-x64")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static ToolCallResult HandleCefStatus(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        var rep = CefModernizationService.GetStatus(targetDir);
        return ToolCallResult.Success(new
        {
            action = "cef_status",
            cefDirectory = rep.CefDirectory,
            isActive = rep.IsActive,
            isDeactivated = rep.IsDeactivated,
            totalActiveSizeBytes = rep.TotalActiveSizeBytes,
            totalActiveSizeMb = rep.TotalActiveSizeBytes / 1024.0 / 1024.0,
            totalReclaimedSizeBytes = rep.TotalReclaimedSizeBytes,
            totalReclaimedSizeMb = rep.TotalReclaimedSizeBytes / 1024.0 / 1024.0,
            activeFileCount = rep.ActiveFileCount,
            deactivatedFileCount = rep.DeactivatedFileCount,
            estimatedRamSavingsMb = rep.EstimatedRamSavingsBytes / 1024.0 / 1024.0,
            clientProcessRunning = rep.ClientProcessRunning,
            runningProcesses = rep.RunningProcesses,
            message = rep.Message,
            targets = rep.Targets.Select(t => new
            {
                relativePath = t.RelativePath,
                exists = t.Exists,
                sizeBytes = t.SizeBytes,
                hasBak = t.HasBak,
                hasBakOld = t.HasBakOld,
                status = t.Status,
                sha256 = t.Sha256Hash
            })
        });
    }

    private static ToolCallResult HandleCefDeactivate(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        bool skipProcessCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out var spc1)) skipProcessCheck = spc1.GetBoolean();
        else if (arguments.TryGetProperty("skip_process_check", out var spc2)) skipProcessCheck = spc2.GetBoolean();

        try
        {
            var res = CefModernizationService.Deactivate(targetDir, skipProcessCheck);
            return res.Success
                ? ToolCallResult.Success(new
                {
                    action = "cef_deactivate",
                    success = true,
                    message = res.Message,
                    deactivatedCount = res.DeactivatedCount,
                    reclaimedDiskMb = res.ReclaimedDiskBytes / 1024.0 / 1024.0,
                    estimatedRamSavingsMb = res.EstimatedRamSavingsBytes / 1024.0 / 1024.0,
                    deactivatedPaths = res.DeactivatedPaths,
                    rotatedBakOldPaths = res.RotatedBakOldPaths
                })
                : ToolCallResult.Error(res.Message);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"CEF deactivation failed: {ex.Message}");
        }
    }

    private static ToolCallResult HandleCefRollback(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        string tier = "previous";
        if (arguments.TryGetProperty("tier", out var tElem) && !string.IsNullOrWhiteSpace(tElem.GetString()))
        {
            tier = tElem.GetString()!.Trim();
        }

        bool skipProcessCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out var spc1)) skipProcessCheck = spc1.GetBoolean();
        else if (arguments.TryGetProperty("skip_process_check", out var spc2)) skipProcessCheck = spc2.GetBoolean();

        try
        {
            var res = CefModernizationService.Rollback(targetDir, tier, skipProcessCheck);
            return res.Success
                ? ToolCallResult.Success(new
                {
                    action = "cef_rollback",
                    success = true,
                    tier,
                    message = res.Message,
                    restoredCount = res.RestoredCount,
                    restoredPaths = res.RestoredPaths
                })
                : ToolCallResult.Error(res.Message);
        }
        catch (Exception ex)
        {
            return ToolCallResult.Error($"CEF rollback failed: {ex.Message}");
        }
    }
}
