// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PostMatchParquetIngestorService.cs
// System: COMPEL Supervision - Dedicated Server Post-Match Parquet Ingestor
// Policy: Strict Explicit Typing (NO 'var'), Native AOT Trim-Safe
// ============================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ASPIRE.Common.Constants;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using Kongor.Core.Analytics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     High-performance, event-driven service that captures dedicated server combat logs
///     upon match completion, converts high-frequency game events into columnar Apache Parquet containers,
///     and queues them for cloud lakehouse synchronization.
/// </summary>
public sealed class PostMatchParquetIngestorService : IHostedService
{
    private readonly MatchServerManagerSupervisor? _supervisor;
    private readonly MatchParquetExportService _exportService;
    private readonly MatchLakehouseStorageUploader _uploader;
    private readonly ArtefactsLocator _artefacts;
    private readonly ILogger<PostMatchParquetIngestorService>? _logger;
    private readonly bool _isQueueArcadeMode;

    private readonly ConcurrentDictionary<long, byte> _processedMatches = new();

    public PostMatchParquetIngestorService(
        MatchServerManagerSupervisor? supervisor,
        MatchParquetExportService exportService,
        MatchLakehouseStorageUploader uploader,
        ArtefactsLocator artefacts,
        ILogger<PostMatchParquetIngestorService>? logger = null,
        IOptions<MatchServerManagerOptions>? options = null)
    {
        _supervisor = supervisor;
        _exportService = exportService;
        _uploader = uploader;
        _artefacts = artefacts;
        _logger = logger;
        _isQueueArcadeMode = options?.Value.QueueArcadeMode ?? false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isQueueArcadeMode)
        {
            _logger?.LogInformation("[POST_MATCH_INGESTOR] QueueArcadeMode=true; skipping Ranked post-match Parquet/Lakehouse listener.");
            return Task.CompletedTask;
        }

        if (_supervisor != null)
        {
            _supervisor.MatchEnded += OnMatchEndedAsync;
            _logger?.LogInformation("[POST_MATCH_INGESTOR] Registered event-driven listener on MatchEnded.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_isQueueArcadeMode)
        {
            return Task.CompletedTask;
        }

        if (_supervisor != null)
        {
            _supervisor.MatchEnded -= OnMatchEndedAsync;
            _logger?.LogInformation("[POST_MATCH_INGESTOR] Detached listener on MatchEnded.");
        }
        return Task.CompletedTask;
    }

    public async Task HandleMatchEndedAsync(DedicatedServerWorker worker)
    {
        if (_isQueueArcadeMode)
        {
            return;
        }

        await OnMatchEndedAsync(worker);
    }

    private async Task OnMatchEndedAsync(DedicatedServerWorker worker)
    {
        long matchId = worker.MatchId;
        if (matchId <= 0)
        {
            _logger?.LogDebug("[POST_MATCH_INGESTOR] Worker on port {Port} ended with unassigned MatchId", worker.Port);
            return;
        }

        try
        {
            await IngestMatchAsync(matchId, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[POST_MATCH_INGESTOR] Error during post-match ingestion for Match #{MatchId}", matchId);
        }
    }

    /// <summary>
    ///     Ingests a match by locating its server combat log, parsing combat events,
    ///     writing the local Parquet container, and triggering cloud upload.
    /// </summary>
    public async Task<bool> IngestMatchAsync(
        long matchId,
        string? explicitLogPath = null,
        CancellationToken cancellationToken = default)
    {
        if (matchId <= 0) return false;

        if (_processedMatches.ContainsKey(matchId))
        {
            _logger?.LogDebug("[POST_MATCH_INGESTOR] Match #{MatchId} already ingested; skipping.", matchId);
            return true;
        }

        string? logFilePath = explicitLogPath ?? FindLogFileForMatch(matchId);
        List<MatchCombatEventRecord> events = [];

        if (!string.IsNullOrWhiteSpace(logFilePath) && File.Exists(logFilePath))
        {
            events = await ParseCombatLogAsync(matchId, logFilePath, cancellationToken);
        }
        else
        {
            _logger?.LogWarning("[POST_MATCH_INGESTOR] No combat log file found on disk for Match #{MatchId}", matchId);
        }

        if (events.Count == 0)
        {
            _logger?.LogWarning("[POST_MATCH_INGESTOR] Generating minimal fallback header event for Match #{MatchId}", matchId);
            events.Add(new MatchCombatEventRecord(
                MatchId: matchId,
                MatchTimeMs: 0,
                Tick: 0,
                EventType: CombatEventType.GoldTick,
                SourcePlayerIndex: 0,
                SourceHeroId: 0,
                TargetPlayerIndex: 0,
                TargetHeroId: 0,
                AbilityOrItemId: 0,
                EventValue: 0f,
                PositionX: 0f,
                PositionY: 0f,
                PositionZ: 0f));
        }

        string baseDir = AppContext.BaseDirectory;
        string outputDirectory = Path.Combine(baseDir, "data", "analytics", "replays");
        string destinationFilePath = Path.Combine(outputDirectory, $"match_{matchId}.parquet");

        BloatReductionReport report = await _exportService.ExportToParquetFileAsync(matchId, events, destinationFilePath, cancellationToken).ConfigureAwait(false);
        _processedMatches[matchId] = 1;

        _logger?.LogInformation(
            "[POST_MATCH_INGESTOR] Successfully generated Parquet lakehouse file for Match #{MatchId} ({Events:N0} events, Saved {Saved:F1}%)",
            matchId, events.Count, report.ReductionPercentage);

        _uploader.EnqueueUpload(matchId, destinationFilePath);
        return true;
    }

    /// <summary>
    ///     Parses the dedicated server log file into structured MatchCombatEventRecord items.
    /// </summary>
    public async Task<List<MatchCombatEventRecord>> ParseCombatLogAsync(
        long matchId,
        string logFilePath,
        CancellationToken cancellationToken = default)
    {
        List<MatchCombatEventRecord> events = [];
        Dictionary<int, short> slotToHeroId = [];

        // K2 dedicated server logs on Windows are typically UTF-16LE if .clog, or UTF-8 if mirrored
        Encoding encoding = logFilePath.EndsWith(".clog", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Unicode
            : Encoding.UTF8;

        try
        {
            await using FileStream fs = new(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(fs, encoding);

            string? line;
            int tickCounter = 0;

            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ReadOnlySpan<char> span = line.AsSpan().Trim();
                tickCounter += 10;

                // 1. Hero Selection / Picks
                if (FastCombatLogParser.TryParsePlayerSelect(span, out FastCombatLogParser.PlayerSelectEvent pSel))
                {
                    short heroId = ResolveHeroIdFromIdentifier(pSel.HeroIdentifier);
                    slotToHeroId[pSel.PlayerSlot] = heroId;
                }
                else if (FastCombatLogParser.TryParseHeroPick(span, out FastCombatLogParser.HeroPickEvent hPick))
                {
                    short heroId = ResolveHeroIdFromIdentifier(hPick.HeroIdentifier);
                    slotToHeroId[hPick.PlayerSlot] = heroId;
                }

                // 2. Kills
                if (FastCombatLogParser.TryParseKill(span, out FastCombatLogParser.KillEvent kill))
                {
                    string targetStr = kill.Target.ToString();
                    bool isHeroKill = targetStr.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase);
                    bool isTowerKill = targetStr.StartsWith("Building_", StringComparison.OrdinalIgnoreCase);
                    bool isBossKill = targetStr.Contains("Kongor", StringComparison.OrdinalIgnoreCase) ||
                                      targetStr.Contains("Boss_", StringComparison.OrdinalIgnoreCase);

                    CombatEventType type = isHeroKill ? CombatEventType.HeroKill
                        : isTowerKill ? CombatEventType.TowerKill
                        : isBossKill ? CombatEventType.RoshKill
                        : CombatEventType.HeroKill;

                    short targetHeroId = isHeroKill ? ResolveHeroIdFromIdentifier(kill.Target) : (short)0;
                    short sourceHeroId = ResolveHeroIdFromIdentifier(kill.Attacker);

                    events.Add(new MatchCombatEventRecord(
                        MatchId: matchId,
                        MatchTimeMs: kill.TimeMs,
                        Tick: tickCounter,
                        EventType: type,
                        SourcePlayerIndex: (byte)(kill.Team == 1 ? 0 : 5),
                        SourceHeroId: sourceHeroId,
                        TargetPlayerIndex: (byte)(kill.Team == 1 ? 5 : 0),
                        TargetHeroId: targetHeroId,
                        AbilityOrItemId: 0,
                        EventValue: 450f,
                        PositionX: kill.X,
                        PositionY: kill.Y,
                        PositionZ: kill.Z));
                }

                // 3. Ability Activations
                else if (FastCombatLogParser.TryParseAbilityActivate(span, out FastCombatLogParser.AbilityActivateEvent abAct))
                {
                    short heroId = slotToHeroId.TryGetValue(abAct.PlayerSlot, out short id) ? id : (short)0;
                    events.Add(new MatchCombatEventRecord(
                        MatchId: matchId,
                        MatchTimeMs: abAct.TimeMs,
                        Tick: tickCounter,
                        EventType: CombatEventType.AbilityCast,
                        SourcePlayerIndex: (byte)abAct.PlayerSlot,
                        SourceHeroId: heroId,
                        TargetPlayerIndex: 0,
                        TargetHeroId: 0,
                        AbilityOrItemId: HashStringToId(abAct.AbilityName),
                        EventValue: 120f,
                        PositionX: abAct.X,
                        PositionY: abAct.Y,
                        PositionZ: abAct.Z));
                }

                // 4. Item Activations (Wards)
                else if (FastCombatLogParser.TryParseItemActivate(span, out FastCombatLogParser.ItemActivateEvent itemAct))
                {
                    ReadOnlySpan<char> item = itemAct.ItemName;
                    bool isWard = item.Contains("Ward", StringComparison.OrdinalIgnoreCase) ||
                                  item.Contains("Eye", StringComparison.OrdinalIgnoreCase);

                    if (isWard)
                    {
                        short heroId = slotToHeroId.TryGetValue(itemAct.PlayerSlot, out short id) ? id : (short)0;
                        events.Add(new MatchCombatEventRecord(
                            MatchId: matchId,
                            MatchTimeMs: itemAct.TimeMs,
                            Tick: tickCounter,
                            EventType: CombatEventType.WardPlaced,
                            SourcePlayerIndex: (byte)itemAct.PlayerSlot,
                            SourceHeroId: heroId,
                            TargetPlayerIndex: 0,
                            TargetHeroId: 0,
                            AbilityOrItemId: HashStringToId(item),
                            EventValue: 100f,
                            PositionX: itemAct.X,
                            PositionY: itemAct.Y,
                            PositionZ: itemAct.Z));
                    }
                }

                // 5. Item Purchases
                else if (FastCombatLogParser.TryParseItemPurchase(span, out FastCombatLogParser.ItemPurchaseEvent itemPurch))
                {
                    short heroId = slotToHeroId.TryGetValue(itemPurch.PlayerSlot, out short id) ? id : (short)0;
                    events.Add(new MatchCombatEventRecord(
                        MatchId: matchId,
                        MatchTimeMs: itemPurch.TimeMs,
                        Tick: tickCounter,
                        EventType: CombatEventType.ItemPurchased,
                        SourcePlayerIndex: (byte)itemPurch.PlayerSlot,
                        SourceHeroId: heroId,
                        TargetPlayerIndex: 0,
                        TargetHeroId: 0,
                        AbilityOrItemId: HashStringToId(itemPurch.ItemName),
                        EventValue: 500f,
                        PositionX: itemPurch.X,
                        PositionY: itemPurch.Y,
                        PositionZ: itemPurch.Z));
                }

                // 6. Gold Earned
                else if (FastCombatLogParser.TryParseGoldEarned(span, out FastCombatLogParser.GoldEarnedEvent goldEarn))
                {
                    short heroId = slotToHeroId.TryGetValue(goldEarn.PlayerSlot, out short id) ? id : (short)0;
                    events.Add(new MatchCombatEventRecord(
                        MatchId: matchId,
                        MatchTimeMs: goldEarn.TimeMs,
                        Tick: tickCounter,
                        EventType: CombatEventType.GoldTick,
                        SourcePlayerIndex: (byte)goldEarn.PlayerSlot,
                        SourceHeroId: heroId,
                        TargetPlayerIndex: 0,
                        TargetHeroId: 0,
                        AbilityOrItemId: 0,
                        EventValue: (float)goldEarn.Gold,
                        PositionX: goldEarn.X,
                        PositionY: goldEarn.Y,
                        PositionZ: goldEarn.Z));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[POST_MATCH_INGESTOR] Error reading combat log {Path}", logFilePath);
        }

        return events;
    }

    private string? FindLogFileForMatch(long matchId)
    {
        List<string> candidateDirectories = [];

        if (!string.IsNullOrWhiteSpace(_artefacts.ArtefactsDirectory))
        {
            candidateDirectories.Add(Path.Combine(_artefacts.ArtefactsDirectory, "game", "logs"));
            candidateDirectories.Add(Path.Combine(_artefacts.ArtefactsDirectory, "game", "logs", "manager"));
            candidateDirectories.Add(_artefacts.ArtefactsDirectory);
        }

        string baseDir = AppContext.BaseDirectory;
        candidateDirectories.Add(Path.Combine(baseDir, "logs"));
        candidateDirectories.Add(Path.Combine(baseDir, "logs", "manager"));
        candidateDirectories.Add(Path.Combine(Directory.GetCurrentDirectory(), "games", "server", "logs"));

        string matchSuffix = $"M{matchId}";

        foreach (string dir in candidateDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            string[] files = Directory.GetFiles(dir, $"*{matchSuffix}*", SearchOption.TopDirectoryOnly);
            foreach (string file in files)
            {
                if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".clog", StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static short ResolveHeroIdFromIdentifier(ReadOnlySpan<char> identifier)
    {
        if (identifier.IsEmpty) return 0;
        string clean = identifier.Trim().ToString();
        if (clean.StartsWith("hero_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[5..];
        }

        // Canonical hero ID mappings for prominent Heroes of Newerth champions
        return clean.ToLowerInvariant() switch
        {
            "pyromancer" => 42,
            "swiftblade" => 15,
            "devourer" => 10,
            "valkyrie" => 48,
            "magmus" => 23,
            "glacius" => 31,
            "witchslayer" or "witch_slayer" => 35,
            "pebbles" => 52,
            "deadwood" => 60,
            "dementedshaman" or "demented_shaman" => 72,
            "bubbles" => 9,
            "wretchedhag" or "wretched_hag" or "babayaga" => 38,
            "legionnaire" => 84,
            "chronos" => 21,
            "scout" => 126,
            "magebane" => 75,
            _ => (short)Math.Abs(clean.GetHashCode() % 1000)
        };
    }

    private static int HashStringToId(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty) return 0;
        return Math.Abs(string.GetHashCode(str, StringComparison.OrdinalIgnoreCase));
    }
}
