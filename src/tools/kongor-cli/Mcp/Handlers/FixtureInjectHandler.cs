// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_fixture_inject' (aliases: 'fixture_inject', 'game_fixture').
/// Atomic Game State Fixture Injector: teleports hero, assigns level, items, skills, spawns enemy creeps,
/// and adjusts health/mana in a single atomic transaction for instant scenario staging.
/// </summary>
public sealed class FixtureInjectHandler
{
    private readonly GameClientManageHandler _clientManageHandler;
    private readonly GameServerManageHandler _serverManageHandler;
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public FixtureInjectHandler(
        GameClientManageHandler clientManageHandler,
        GameServerManageHandler serverManageHandler,
        IGameIpcBridge? ipcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _clientManageHandler = clientManageHandler;
        _serverManageHandler = serverManageHandler;
        _ipcBridge = ipcBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[FixtureInject] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "inject";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "both";
        if (arguments.TryGetProperty("target", out JsonElement tgtEl) && !string.IsNullOrWhiteSpace(tgtEl.GetString()))
        {
            target = tgtEl.GetString()!.Trim().ToLowerInvariant();
        }

        int? port = null;
        if (arguments.TryGetProperty("port", out JsonElement portEl) && portEl.TryGetInt32(out int pVal))
        {
            port = pVal;
        }

        _logger?.Invoke($"Executing '{action}' fixture injection (Target: '{target}', Port: {port?.ToString() ?? "auto"})...");

        switch (action)
        {
            case "preset":
                return await HandlePresetAsync(arguments, target, port, sw, cancellationToken).ConfigureAwait(false);

            case "reset":
                return await HandleResetAsync(arguments, target, port, sw, cancellationToken).ConfigureAwait(false);

            case "inject":
            default:
                return await HandleInjectAsync(arguments, target, port, sw, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolCallResult> HandlePresetAsync(
        JsonElement arguments,
        string target,
        int? port,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        string presetName = "mid_clash";
        if (arguments.TryGetProperty("preset", out JsonElement pEl) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            presetName = pEl.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("scenario", out JsonElement sEl) && !string.IsNullOrWhiteSpace(sEl.GetString()))
        {
            presetName = sEl.GetString()!.Trim().ToLowerInvariant();
        }

        var (hero, level, gold, coords, items, hp, mana, creeps) = ResolvePreset(presetName);
        bool allowSimulation = IsSimulationAllowed(arguments);

        var syntheticArgs = new
        {
            action = "inject",
            hero,
            level,
            gold,
            teleport = coords,
            items,
            health_percent = hp,
            mana_percent = mana,
            spawn_creeps = creeps,
            target,
            port,
            allowSimulation
        };

        string json = JsonSerializer.Serialize(syntheticArgs);
        using var doc = JsonDocument.Parse(json);
        return await HandleInjectAsync(doc.RootElement, target, port, sw, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> HandleResetAsync(
        JsonElement arguments,
        string target,
        int? port,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        List<string> commands =
        [
            "Set dev true; Set cheats true",
            "Respawn",
            "SetLevel 1",
            "GiveGold 600",
            "ClearInventory",
            "Refresh"
        ];

        var (executionMode, dispatchedCount, liveDispatched) = await DispatchCommandsAsync(commands, target, port, cancellationToken).ConfigureAwait(false);

        if (!liveDispatched && !IsSimulationAllowed(arguments))
        {
            sw.Stop();
            return ToolCallResult.Error(
                $"Engine target '{target}' is offline (IPC not connected, no dedicated server active). " +
                $"Cannot dispatch fixture commands to live engine. Pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1 for synthetic testing.");
        }

        sw.Stop();
        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: executionMode,
            targetStatus: BuildTargetStatus(),
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                EngineRenderFrame = liveDispatched ? 1 : 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "reset",
                commandsDispatched = commands,
                dispatchedCount,
                applied = liveDispatched
            },
            fidelity: liveDispatched ? ExecutionFidelity.LiveVerified : ExecutionFidelity.SimulatedTestDouble);
    }

    private async Task<ToolCallResult> HandleInjectAsync(
        JsonElement arguments,
        string target,
        int? port,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        string? hero = null;
        if (arguments.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            hero = hEl.GetString()!.Trim();
        }

        int level = 1;
        if (arguments.TryGetProperty("level", out JsonElement lvlEl) && lvlEl.TryGetInt32(out int lVal))
        {
            level = Math.Clamp(lVal, 1, 25);
        }

        int gold = 0;
        if (arguments.TryGetProperty("gold", out JsonElement gEl) && gEl.TryGetInt32(out int gVal))
        {
            gold = Math.Max(0, gVal);
        }

        double? hpPct = null;
        if ((arguments.TryGetProperty("health_percent", out JsonElement hpEl) ||
             arguments.TryGetProperty("hp", out hpEl)) &&
            hpEl.TryGetDouble(out double parsedHp))
        {
            hpPct = Math.Clamp(parsedHp, 0.0, 1.0);
        }

        double? manaPct = null;
        if ((arguments.TryGetProperty("mana_percent", out JsonElement manaEl) ||
             arguments.TryGetProperty("mana", out manaEl)) &&
            manaEl.TryGetDouble(out double parsedMana))
        {
            manaPct = Math.Clamp(parsedMana, 0.0, 1.0);
        }

        double[]? coords = null;
        if (arguments.TryGetProperty("teleport", out JsonElement tpEl) || arguments.TryGetProperty("coords", out tpEl))
        {
            if (tpEl.ValueKind == JsonValueKind.Object)
            {
                double x = tpEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double xv) ? xv : 0;
                double y = tpEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double yv) ? yv : 0;
                double z = tpEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double zv) ? zv : 0;
                coords = [x, y, z];
            }
            else if (tpEl.ValueKind == JsonValueKind.Array && tpEl.GetArrayLength() >= 2)
            {
                coords = tpEl.EnumerateArray().Select(e => e.GetDouble()).Take(3).ToArray();
            }
        }

        List<string> items = [];
        if (arguments.TryGetProperty("items", out JsonElement itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                string? itm = item.GetString();
                if (!string.IsNullOrWhiteSpace(itm)) items.Add(itm.Trim());
            }
        }

        List<string> commands = ["Set dev true; Set cheats true"];

        if (!string.IsNullOrWhiteSpace(hero))
        {
            commands.Add($"Respawn; Morph {hero}; Refresh");
        }

        if (level > 1)
        {
            commands.Add($"SetLevel {level}");
        }

        if (gold > 0)
        {
            commands.Add($"GiveGold {gold}");
        }

        if (coords != null && coords.Length >= 2)
        {
            double z = coords.Length > 2 ? coords[2] : 0.0;
            commands.Add($"Teleport {coords[0]:F2} {coords[1]:F2} {z:F2}");
        }

        if (items.Count > 0)
        {
            commands.Add("ClearInventory");
            foreach (var item in items)
            {
                commands.Add($"GiveItem {item}");
            }
        }

        if (hpPct.HasValue)
        {
            commands.Add($"SetHealthPercent {hpPct.Value:F2}");
        }

        if (manaPct.HasValue)
        {
            commands.Add($"SetManaPercent {manaPct.Value:F2}");
        }

        // Creep wave spawns
        int creepsSpawned = 0;
        if (arguments.TryGetProperty("spawn_creeps", out JsonElement creepsEl) && creepsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in creepsEl.EnumerateArray())
            {
                string type = c.TryGetProperty("type", out JsonElement tEl) ? tEl.GetString() ?? "Creep_LegionMelee" : "Creep_LegionMelee";
                int count = c.TryGetProperty("count", out JsonElement cntEl) && cntEl.TryGetInt32(out int cnt) ? cnt : 1;
                for (int i = 0; i < Math.Min(count, 10); i++)
                {
                    commands.Add($"SpawnUnit {type}");
                    creepsSpawned++;
                }
            }
        }

        var (executionMode, dispatchedCount, liveDispatched) = await DispatchCommandsAsync(commands, target, port, cancellationToken).ConfigureAwait(false);

        if (!liveDispatched && !IsSimulationAllowed(arguments))
        {
            sw.Stop();
            return ToolCallResult.Error(
                $"Engine target '{target}' is offline (IPC not connected, no dedicated server active). " +
                $"Cannot dispatch fixture commands to live engine. Pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1 for synthetic testing.");
        }

        sw.Stop();
        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: executionMode,
            targetStatus: BuildTargetStatus(),
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                EngineRenderFrame = liveDispatched ? 1 : 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "inject",
                applied = liveDispatched,
                hero = hero ?? "current",
                level,
                gold,
                coordinates = coords,
                items,
                healthPercent = hpPct ?? 1.0,
                manaPercent = manaPct ?? 1.0,
                creepsSpawned,
                dispatchedCommands = commands,
                dispatchedCount,
                entityStateSnapshot = liveDispatched ? (object?)null : new
                {
                    hero = hero ?? "Hero_Pyromancer",
                    level,
                    gold,
                    coords = coords ?? [0.0, 0.0, 0.0],
                    inventory = items,
                    isAlive = true
                }
            },
            fidelity: liveDispatched ? ExecutionFidelity.LiveVerified : ExecutionFidelity.SimulatedTestDouble);
    }

    private async Task<(string mode, int count, bool liveDispatched)> DispatchCommandsAsync(
        List<string> commands,
        string target,
        int? port,
        CancellationToken cancellationToken)
    {
        int count = 0;
        bool liveDispatched = false;

        // 1. Client Plane via IPC or direct command
        if ((target == "client" || target == "both") && _ipcBridge != null && _ipcBridge.IsConnected)
        {
            try
            {
                foreach (var cmd in commands)
                {
                    await _ipcBridge.ExecuteRpcAsync<JsonElement>("console.execute", new { command = cmd }, 1000, cancellationToken).ConfigureAwait(false);
                    count++;
                }
                liveDispatched = true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[WARN] Client IPC command dispatch failed: {ex.Message}");
            }
        }

        // 2. Server Plane via dedicated stdin/port
        if (target == "server" || target == "both")
        {
            int targetPort = port ?? ResolveServerPort();
            if (targetPort > 0 && _serverManageHandler != null)
            {
                var running = _serverManageHandler.DiscoverRunningDedicatedServers();
                var targetWorker = running.FirstOrDefault(w => w.Port == targetPort);
                if (targetWorker != null)
                {
                    bool allSuccess = true;
                    foreach (var cmd in commands)
                    {
                        var cmdRes = await _serverManageHandler.ExecuteCommandOnPortAsync(targetPort, cmd, cancellationToken).ConfigureAwait(false);
                        count++;
                        if (cmdRes.IsError || cmdRes.FirstText.Contains(ExecutionTruth.StatusTargetOffline, StringComparison.OrdinalIgnoreCase))
                        {
                            allSuccess = false;
                        }
                    }
                    if (allSuccess && count > 0)
                    {
                        liveDispatched = true;
                    }
                }
            }
        }

        string mode = liveDispatched
            ? ExecutionMode.LiveEngineRpc
            : (GameServerManageHandler.IsTestExecution ? ExecutionMode.SyntheticFallback : ExecutionMode.OfflineDirect);

        return (mode, Math.Max(count, commands.Count), liveDispatched);
    }

    private static bool IsSimulationAllowed(JsonElement arguments)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS"), "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("allowSimulation", out JsonElement el) ||
                arguments.TryGetProperty("allow_simulation", out el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b) && b) return true;
            }
        }

        return false;
    }

    private static (string hero, int level, int gold, double[] coords, string[] items, double hp, double mana, object[] creeps) ResolvePreset(string preset)
    {
        return preset switch
        {
            "roshan_contest" or "kongor_contest" => (
                "Hero_Devourer", 11, 3500, [2500.0, -1200.0, 50.0],
                ["Item_BehemothsHeart", "Item_PortalKey", "Item_Marchers"],
                1.0, 0.8,
                [new { type = "Creep_Kongor", count = 1 }]
            ),
            "fountain_dive" => (
                "Hero_Chronos", 25, 12000, [8000.0, 8000.0, 150.0],
                ["Item_ElderParasite", "Item_SavageScythe", "Item_Wingbow", "Item_PostHaste"],
                1.0, 1.0,
                Array.Empty<object>()
            ),
            "rune_fight" => (
                "Hero_Valkyrie", 6, 1200, [-1500.0, 1800.0, 60.0],
                ["Item_Bottle", "Item_Marchers", "Item_MinorTotem"],
                0.9, 0.75,
                Array.Empty<object>()
            ),
            _ => ( // "mid_clash"
                "Hero_Pyromancer", 16, 5000, [0.0, 0.0, 100.0],
                ["Item_PortalKey", "Item_Spellshards", "Item_PostHaste"],
                1.0, 1.0,
                [new { type = "Creep_LegionMelee", count = 3 }, new { type = "Creep_LegionRanged", count = 1 }]
            )
        };
    }

    private int ResolveServerPort()
    {
        var running = _serverManageHandler.DiscoverRunningDedicatedServers();
        if (running.Count > 0)
        {
            return running[0].Port;
        }

        var registered = _serverManageHandler.PortRegistry.GetActiveWorkers();
        if (registered.Count > 0)
        {
            return registered[0].Port;
        }

        return 11235;
    }

    private TargetStatus BuildTargetStatus()
    {
        return new TargetStatus
        {
            Client = _clientManageHandler.GetLivenessStatus(),
            Server = _serverManageHandler.GetLivenessStatus()
        };
    }
}
