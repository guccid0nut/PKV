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
    private static ToolCallResult HandleSecurityStatus(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        if (!Directory.Exists(targetDir))
        {
            return ToolCallResult.Error($"Target directory not found at: {targetDir}");
        }

        TargetFleet fleet = ResolveFleet(arguments);
        var report = NativeSecurityUpgradeService.GetSecurityStatus(targetDir, fleet);

        var payload = new
        {
            success = true,
            action = "security_status",
            gameDirectory = targetDir,
            fleet = fleet.ToString(),
            allTargetsHealthy = report.AllTargetsHealthy,
            gameProcessesRunning = report.GameProcessesRunning,
            runningProcesses = report.RunningProcesses,
            targetCount = report.Targets.Count,
            targets = report.Targets.Select(t => new
            {
                relativePath = t.RelativePath,
                fullPath = t.FullPath,
                binaryName = t.BinaryName,
                exists = t.Exists,
                sizeBytes = t.SizeBytes,
                fileVersion = t.FileVersion,
                productVersion = t.ProductVersion,
                sha256 = t.Sha256Hash,
                hasBak = t.HasBak,
                hasBakOld = t.HasBakOld,
                isTls13Supported = t.IsTls13Supported,
                cveRemediationStatus = t.CveRemediationStatus,
                isParityValid = t.IsParityValid,
                missingSymbols = t.MissingSymbols
            }).ToList()
        };

        return ToolCallResult.Success(payload);
    }

    private static ToolCallResult HandleSecurityUpgrade(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        if (!Directory.Exists(targetDir))
        {
            return ToolCallResult.Error($"Target directory not found at: {targetDir}");
        }

        bool skipProcessCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out var spc1)) skipProcessCheck = spc1.GetBoolean();
        else if (arguments.TryGetProperty("skip_process_check", out var spc2)) skipProcessCheck = spc2.GetBoolean();

        bool skipExportCheck = false;
        if (arguments.TryGetProperty("skipExportCheck", out var sec1)) skipExportCheck = sec1.GetBoolean();
        else if (arguments.TryGetProperty("skip_export_check", out var sec2)) skipExportCheck = sec2.GetBoolean();

        bool redirectIat = false;
        if (arguments.TryGetProperty("redirectIat", out var riat1)) redirectIat = riat1.GetBoolean();
        else if (arguments.TryGetProperty("redirect_iat", out var riat2)) redirectIat = riat2.GetBoolean();

        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            return ToolCallResult.Error($"Cannot upgrade security binaries while game processes are active ({runningProcs}). Close them first or pass 'skipProcessCheck: true'.");
        }

        TargetFleet fleet = ResolveFleet(arguments);
        var logList = new List<string>();
        var result = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
            targetDir,
            null,
            skipProcessCheck,
            skipExportCheck,
            redirectIat,
            fleet,
            logList.Add);

        var payload = new
        {
            success = result.Success,
            action = "security_upgrade",
            fleet = fleet.ToString(),
            message = result.Message,
            upgradedPaths = result.UpgradedPaths,
            rotatedBakOldPaths = result.RotatedBakOldPaths,
            redirectedIatPaths = result.RedirectedIatPaths ?? Array.Empty<string>(),
            logs = logList,
            status = NativeSecurityUpgradeService.GetSecurityStatus(targetDir, fleet)
        };

        return result.Success ? ToolCallResult.Success(payload) : ToolCallResult.Error(result.Message);
    }

    private static ToolCallResult HandleSecurityRollback(string repoRoot, JsonElement arguments)
    {
        string targetDir = ResolveGameDirectory(repoRoot, arguments);
        if (!Directory.Exists(targetDir))
        {
            return ToolCallResult.Error($"Target directory not found at: {targetDir}");
        }

        string tier = "previous";
        if (arguments.TryGetProperty("tier", out var tElem) && !string.IsNullOrWhiteSpace(tElem.GetString()))
        {
            tier = tElem.GetString()!.Trim();
        }

        bool skipProcessCheck = false;
        if (arguments.TryGetProperty("skipProcessCheck", out var spc1)) skipProcessCheck = spc1.GetBoolean();
        else if (arguments.TryGetProperty("skip_process_check", out var spc2)) skipProcessCheck = spc2.GetBoolean();

        if (!skipProcessCheck && NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            return ToolCallResult.Error($"Cannot rollback security binaries while game processes are active ({runningProcs}). Close them first or pass 'skipProcessCheck: true'.");
        }

        TargetFleet fleet = ResolveFleet(arguments);
        var logList = new List<string>();
        var result = NativeSecurityUpgradeService.RollbackSecurityBinaries(targetDir, tier, skipProcessCheck, fleet, logList.Add);

        var payload = new
        {
            success = result.Success,
            action = "security_rollback",
            fleet = fleet.ToString(),
            tier = result.TargetTier,
            message = result.Message,
            restoredPaths = result.RestoredPaths,
            logs = logList
        };

        return result.Success ? ToolCallResult.Success(payload) : ToolCallResult.Error(result.Message);
    }

    private static async Task<ToolCallResult> HandleTlsVerifyAsync(JsonElement arguments)
    {
        string url = "https://masterserver.naeu.heroesofnewerth.com";
        if (arguments.TryGetProperty("url", out var uElem) && !string.IsNullOrWhiteSpace(uElem.GetString()))
        {
            url = uElem.GetString()!.Trim();
        }

        var result = await NativeSecurityUpgradeService.VerifyTls13HandshakeAsync(url).ConfigureAwait(false);

        var payload = new
        {
            success = result.Success,
            action = "tls_verify",
            targetUrl = result.TargetUrl,
            negotiatedProtocol = result.NegotiatedProtocol,
            cipherSuite = result.CipherSuite,
            roundtripMs = result.RoundtripMs,
            message = result.Message
        };

        return result.Success ? ToolCallResult.Success(payload) : ToolCallResult.Error(result.Message);
    }

}
