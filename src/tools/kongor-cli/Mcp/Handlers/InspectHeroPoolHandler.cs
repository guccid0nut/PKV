// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_inspect_hero_pool' (alias: 'inspect_hero_pool').
/// Performs automated 3-way reconciliation across:
/// 1. Server-side registered hero entities (games/server/override/heroes/*/*.entity)
/// 2. Client-side pick screen grid (heroes_preview.xml / heroes_fe2.interface)
/// 3. Modular Lua definition modules (games/client/override/ui/scripts/newui/definitions/heroes/*.lua)
/// Answers questions like "Why are only 85 heroes showing in pick screen?" by diffing rosters in one call.
/// </summary>
public sealed class InspectHeroPoolHandler
{
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public InspectHeroPoolHandler(
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        bool filterMissingOnly = true;
        if (arguments.TryGetProperty("filter_missing_only", out JsonElement fmoEl) ||
            arguments.TryGetProperty("filterMissingOnly", out fmoEl))
        {
            filterMissingOnly = fmoEl.GetBoolean();
        }

        try
        {
            // 1. Collect Server Hero Entities
            var serverHeroes = CollectServerHeroes();

            // 2. Collect Client Pick Grid Heroes
            var clientHeroes = CollectClientPreviewHeroes();

            // 3. Collect Modular Lua Definitions
            var luaHeroes = CollectModularLuaHeroes();

            // 4. Perform 3-way diff
            var allKnownHeroes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            allKnownHeroes.UnionWith(serverHeroes);
            allKnownHeroes.UnionWith(clientHeroes);
            allKnownHeroes.UnionWith(luaHeroes);

            var missingOnServer = new List<string>();
            var missingOnClient = new List<string>();
            var missingLuaDefinition = new List<string>();
            var fullySynchronized = new List<string>();

            foreach (string hero in allKnownHeroes)
            {
                bool onServer = serverHeroes.Contains(hero);
                bool onClient = clientHeroes.Contains(hero);
                bool hasLua = luaHeroes.Contains(hero);

                if (!onServer && onClient)
                {
                    missingOnServer.Add(hero);
                }

                if (onServer && !onClient)
                {
                    missingOnClient.Add(hero);
                }

                if ((onServer || onClient) && !hasLua)
                {
                    missingLuaDefinition.Add(hero);
                }

                if (onServer && onClient && hasLua)
                {
                    fullySynchronized.Add(hero);
                }
            }

            sw.Stop();
            double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

            _logger?.Invoke($"[HeroPoolInspector] Reconciled {allKnownHeroes.Count} total heroes in {elapsedMs}ms: Server={serverHeroes.Count}, Client={clientHeroes.Count}, Lua={luaHeroes.Count}. Missing on Server: {missingOnServer.Count}, Missing on Client: {missingOnClient.Count}.");

            return Task.FromResult(ToolCallResult.Json(new
            {
                status = (missingOnServer.Count > 0 || missingOnClient.Count > 0) ? "discrepancies_found" : "healthy",
                success = true,
                summary = new
                {
                    totalKnownHeroes = allKnownHeroes.Count,
                    serverEntitiesCount = serverHeroes.Count,
                    clientPreviewCount = clientHeroes.Count,
                    modularLuaCount = luaHeroes.Count,
                    fullySynchronizedCount = fullySynchronized.Count,
                    missingOnServerCount = missingOnServer.Count,
                    missingOnClientCount = missingOnClient.Count,
                    missingLuaDefinitionCount = missingLuaDefinition.Count
                },
                discrepancies = new
                {
                    missingOnServer = missingOnServer,
                    missingOnClient = missingOnClient,
                    missingLuaDefinition = missingLuaDefinition
                },
                rosters = filterMissingOnly ? null : new
                {
                    serverHeroes = serverHeroes.OrderBy(h => h),
                    clientHeroes = clientHeroes.OrderBy(h => h),
                    luaHeroes = luaHeroes.OrderBy(h => h)
                },
                executionTimeMs = elapsedMs,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"[HeroPoolInspector] Failed to reconcile hero pool: {ex.Message}");
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true));
        }
    }

    private HashSet<string> CollectServerHeroes()
    {
        var heroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] candidateDirs = [
            Path.Combine(_repoRoot, "games", "server", "override", "heroes"),
            Path.Combine(_repoRoot, "games", "server", "game", "heroes"),
            Path.Combine(_repoRoot, "games", "client", "override", "heroes"),
            Path.Combine(_repoRoot, "games", "client", "game", "heroes"),
            Path.Combine(_repoRoot, "heroes")
        ];

        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                string heroFolder = Path.GetFileName(subDir);
                if (File.Exists(Path.Combine(subDir, "hero.entity")) ||
                    Directory.GetFiles(subDir, "*.entity", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    string canonicalKey = NormalizeHeroKey(heroFolder);
                    heroes.Add(canonicalKey);
                }
            }
        }

        return heroes;
    }

    private HashSet<string> CollectClientPreviewHeroes()
    {
        var heroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] previewPaths = [
            Path.Combine(_repoRoot, "games", "client", "override", "heroes_preview.xml"),
            Path.Combine(_repoRoot, "games", "client", "game", "heroes_preview.xml"),
            Path.Combine(_repoRoot, "games", "server", "override", "heroes_preview.xml")
        ];

        foreach (string path in previewPaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                XDocument doc = XDocument.Load(path);
                if (doc.Root == null) continue;

                foreach (XElement heroEl in doc.Root.Elements("hero"))
                {
                    string? name = heroEl.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals("Default_Values", StringComparison.OrdinalIgnoreCase))
                    {
                        heroes.Add(NormalizeHeroKey(name));
                    }
                }
                break; // First existing is canonical
            }
            catch { }
        }

        return heroes;
    }

    private HashSet<string> CollectModularLuaHeroes()
    {
        var heroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] candidateDirs = [
            Path.Combine(_repoRoot, "games", "client", "override", "ui", "scripts", "newui", "definitions", "heroes"),
            Path.Combine(_repoRoot, "games", "shared", "override", "ui", "scripts", "newui", "definitions", "heroes"),
            Path.Combine(_repoRoot, "games", "shared", "ui", "scripts", "newui", "definitions", "heroes")
        ];

        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var luaFiles = Directory.GetFiles(dir, "*.lua", SearchOption.TopDirectoryOnly);
            foreach (string file in luaFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(file);
                if (baseName.Equals("avatar_index", StringComparison.OrdinalIgnoreCase)) continue;

                heroes.Add(NormalizeHeroKey(baseName));
            }
            break;
        }

        return heroes;
    }

    private static string NormalizeHeroKey(string name)
    {
        if (name.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            return "Hero_" + char.ToUpperInvariant(name[5]) + (name.Length > 6 ? name[6..] : string.Empty);
        }

        // Convert "pyromancer" -> "Hero_Pyromancer"
        return "Hero_" + char.ToUpperInvariant(name[0]) + (name.Length > 1 ? name[1..] : string.Empty);
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
