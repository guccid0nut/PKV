// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_set_store_mode' (alias: 'set_store_mode').
/// Dynamically toggles Master Server store configuration between:
/// - 'sandbox': UnlockAllItems = true (all 1,400+ avatars, announcers, and couriers available)
/// - 'progression': UnlockAllItems = false (standard store progression with Silver/Gold coin economy)
/// Persists changes to appsettings.json and optionally notifies the live hon_x64.exe client to refresh its cache.
/// </summary>
public sealed class SetStoreModeHandler
{
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public SetStoreModeHandler(
        IGameIpcBridge? ipcBridge = null,
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _ipcBridge = ipcBridge;
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string mode = "sandbox";
        if (arguments.TryGetProperty("mode", out JsonElement mEl) && !string.IsNullOrWhiteSpace(mEl.GetString()))
        {
            mode = mEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool refreshClient = true;
        if (arguments.TryGetProperty("refresh_client", out JsonElement rcEl) ||
            arguments.TryGetProperty("refreshClient", out rcEl))
        {
            refreshClient = rcEl.GetBoolean();
        }

        bool persist = true;
        if (arguments.TryGetProperty("persist", out JsonElement pEl))
        {
            persist = pEl.GetBoolean();
        }

        // Determine current state from Master Server service reflection or appsettings
        bool currentUnlockAll = ReadCurrentUnlockAllSetting();

        bool targetUnlockAll = mode switch
        {
            "status" => currentUnlockAll,
            "toggle" => !currentUnlockAll,
            "progression" => false,
            _ => true // "sandbox" or default
        };

        bool modeChanged = (mode != "status" && targetUnlockAll != currentUnlockAll);

        // 1. Update in-memory static property on UpgradesResolutionService if loaded
        try
        {
            var masterAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Contains("KONGOR", StringComparison.OrdinalIgnoreCase) == true);
            var type = masterAsm?.GetType("KONGOR.MasterServer.Services.Store.UpgradesResolutionService") ??
                       masterAsm?.GetType("Services.Store.UpgradesResolutionService");
            var prop = type?.GetProperty("UnlockAllItems");
            if (prop != null && mode != "status")
            {
                prop.SetValue(null, targetUnlockAll);
                _logger?.Invoke($"[SetStoreModeHandler] Set UpgradesResolutionService.UnlockAllItems = {targetUnlockAll} via reflection.");
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[SetStoreModeHandler] Reflection update warning: {ex.Message}");
        }

        // 2. Persist to appsettings.json
        bool persisted = false;
        if (persist && mode != "status")
        {
            persisted = PersistToAppSettings(targetUnlockAll);
        }

        // 3. Notify live game client over IPC to invalidate/reload store cache
        bool clientRefreshed = false;
        string? clientIpcResponse = null;
        if (refreshClient && _ipcBridge != null && _ipcBridge.IsConnected)
        {
            try
            {
                string luaMode = targetUnlockAll ? "PreservationFreeAll" : "EarnedProgression";
                string luaUnlockBool = targetUnlockAll ? "true" : "false";
                string refreshScript = $@"
SetSave('_kongor_store_mode', '{luaMode}', 'string')
SetSave('_kongor_unlock_all', '{luaUnlockBool}', 'bool')
if HoN_Store and HoN_Store.RefreshCache then
    HoN_Store:RefreshCache()
end
if HeroDefinitionsCache and HeroDefinitionsCache.LoadAllHeroes then
    HeroDefinitionsCache.LoadAllHeroes()
end
return 'STORE_AND_HEROES_REFRESHED'
";
                clientIpcResponse = await _ipcBridge.ExecuteCommandAsync(refreshScript.Trim(), 3000, cancellationToken).ConfigureAwait(false);
                clientRefreshed = true;
                _logger?.Invoke($"[SetStoreModeHandler] Client store cache refreshed via IPC: {clientIpcResponse}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[SetStoreModeHandler] Client store cache refresh failed: {ex.Message}");
            }
        }

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        string effectiveModeName = targetUnlockAll ? "sandbox" : "progression";

        _logger?.Invoke($"[SetStoreModeHandler] Master Server store mode set to '{effectiveModeName}' (UnlockAllItems={targetUnlockAll}, persisted={persisted}, clientRefreshed={clientRefreshed}) in {elapsedMs}ms.");

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            mode = effectiveModeName,
            unlockAllItems = targetUnlockAll,
            previousMode = currentUnlockAll ? "sandbox" : "progression",
            modeChanged,
            persisted,
            clientRefreshed,
            clientIpcResponse,
            description = targetUnlockAll
                ? "Sandbox Mode: all 1,400+ avatars, couriers, announcers, and account upgrades are unlocked for free local testing."
                : "Progression Mode: store enforces account inventory checks and coin/ticket deductions.",
            executionTimeMs = elapsedMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private bool ReadCurrentUnlockAllSetting()
    {
        string[] candidateConfigs = [
            Path.Combine(_repoRoot, "src", "services", "master-server", "appsettings.Development.json"),
            Path.Combine(_repoRoot, "src", "services", "master-server", "appsettings.json")
        ];

        foreach (string path in candidateConfigs)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Store", out var sEl) &&
                    sEl.TryGetProperty("UnlockAllItems", out var uaiEl))
                {
                    return uaiEl.GetBoolean();
                }
                if (doc.RootElement.TryGetProperty("UnlockAllItems", out var rootUai))
                {
                    return rootUai.GetBoolean();
                }
            }
            catch { }
        }

        return true; // Default is sandbox/true
    }

    private bool PersistToAppSettings(bool unlockAll)
    {
        string[] targetConfigs = [
            Path.Combine(_repoRoot, "src", "services", "master-server", "appsettings.json"),
            Path.Combine(_repoRoot, "src", "services", "master-server", "appsettings.Development.json"),
            Path.Combine(_repoRoot, "src", "services", "web-api", "appsettings.json"),
            Path.Combine(_repoRoot, "src", "services", "web-api", "appsettings.Development.json"),
            Path.Combine(_repoRoot, "src", "services", "web-ui", "appsettings.json"),
            Path.Combine(_repoRoot, "src", "services", "web-ui", "appsettings.Development.json")
        ];

        string canonicalMode = unlockAll ? "PreservationFreeAll" : "EarnedProgression";
        bool anyWritten = false;

        foreach (string path in targetConfigs)
        {
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                var node = JsonNode.Parse(json);
                if (node is JsonObject rootObj)
                {
                    if (rootObj["Store"] is JsonObject storeObj)
                    {
                        storeObj["UnlockAllItems"] = unlockAll;
                        storeObj["Mode"] = canonicalMode;
                    }
                    else
                    {
                        rootObj["Store"] = new JsonObject
                        {
                            ["UnlockAllItems"] = unlockAll,
                            ["Mode"] = canonicalMode
                        };
                    }

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string updatedJson = rootObj.ToJsonString(options);
                    File.WriteAllText(path, updatedJson);
                    anyWritten = true;
                    _logger?.Invoke($"[SetStoreModeHandler] Persisted Store:UnlockAllItems={unlockAll} into '{path}'.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[SetStoreModeHandler] Failed to persist to '{path}': {ex.Message}");
            }
        }

        return anyWritten;
    }

    private static string FindRepoRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "games")) &&
                Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
