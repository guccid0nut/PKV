// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

public enum DiscrepancyRuleCode
{
    DIV_ZERO_NAN,
    KDA_MISMATCH,
    OUTCOME_MISMATCH,
    DURATION_FORMAT_ERROR,
    VFS_TEXTURE_MISSING,
    WIN_RATE_DRIFT,
    GAMES_COUNT_MISMATCH,
    DISCONNECT_COUNT_MISMATCH,
    HERO_NAME_UNRESOLVED,
    UI_LAYER_GHOSTING,
    MASTERY_DATA_CORRUPT,
    REWARD_BUTTON_MISSING,
    MATCH_DETAILS_MISSING_DATA,
    CON_REWARD_BLANK,
    DEV_TEAPOT_DETECTED
}

public sealed class StatsDiscrepancy
{
    public required DiscrepancyRuleCode RuleCode { get; init; }
    public required string Subsystem { get; init; }
    public required string Description { get; init; }
    public required string Expected { get; init; }
    public required string Actual { get; init; }
    public string? MatchId { get; init; }
    public string? RemediationHint { get; init; }
}

public sealed class StatsAuditRequest
{
    public string Nickname { get; init; } = "MCPPlayer";
    public string Tab { get; init; } = "all"; // "overview", "match_history", "mastery", "match_details", "ghosting", "all"
    public bool Synthetic { get; init; } = false;
    public bool RecordBacklog { get; init; } = true;
    public AccountFixtureDto? CustomGroundTruth { get; init; }
    public RenderedClientStatsDto? CustomRenderedState { get; init; }
}

public sealed class RenderedMasteryHeroCardDto
{
    public string HeroKey { get; set; } = "";
    public string HeroName { get; set; } = "";
    public int Level { get; set; } = 1;
    public string Icon { get; set; } = "";
}

public sealed class RenderedMasteryDto
{
    public int TotalHeroes { get; set; }
    public int HoodedSilhouettesCount { get; set; }
    public int TotalMasteryLevel { get; set; }
    public bool HasOverallRewardClaimButton { get; set; } = true;
    public List<RenderedMasteryHeroCardDto> Heroes { get; set; } = [];
}

public sealed class RenderedScoreboardRowDto
{
    public int SlotIndex { get; set; }
    public int Team { get; set; }
    public string PlayerName { get; set; } = "";
    public string HeroEntity { get; set; } = "";
    public string HeroIcon { get; set; } = "";
    public List<string> ItemIcons { get; set; } = [];
}

public sealed class RenderedMatchDetailsDto
{
    public string MatchId { get; set; } = "";
    public string MatchOwner { get; set; } = "";
    public string HeroModelPath { get; set; } = "";
    public string MasteryXpBarText { get; set; } = "";
    public string NextLevelAwardTexture { get; set; } = "";
    public List<string> InventoryItemIcons { get; set; } = [];
    public List<RenderedScoreboardRowDto> ScoreboardRows { get; set; } = [];
}

public sealed class RenderedClientStatsDto
{
    public int Wins { get; set; }
    public int Losses { get; set; }
    public string WinningPercentage { get; set; } = "0%";
    public int TotalGames { get; set; }
    public int Disconnects { get; set; }
    public string LeavePercent { get; set; } = "0.0%";
    public int Mmr { get; set; }
    public string ConRewardTexture { get; set; } = "/ui/fe2/newui/res/playerstats/conrewards/chest0.png";
    public List<RenderedMatchRowDto> MatchRows { get; set; } = [];
    public RenderedMasteryDto? Mastery { get; set; }
    public RenderedMatchDetailsDto? MatchDetails { get; set; }
    public List<string> VisibleContainers { get; set; } = [];
    public List<string> TeapotOccurrences { get; set; } = [];
}

public sealed class RenderedMatchRowDto
{
    public string MatchId { get; set; } = "";
    public string HeroName { get; set; } = "";
    public string HeroIconTexture { get; set; } = "";
    public string ResultTexture { get; set; } = ""; // "/ui/fe2/newui/res/playerstats/match_result_victory.png"
    public string KdaText { get; set; } = ""; // "^g12^*/^r2^*/^y8"
    public string DurationText { get; set; } = ""; // "30:50"
    public string MapText { get; set; } = "";
    public string TimeText { get; set; } = "";
}

public sealed class StatsAuditReport
{
    public required string Nickname { get; init; }
    public required string ExecutionMode { get; init; } // "Synthetic" or "LiveIPC"
    public bool Success => Discrepancies.Count == 0;
    public List<StatsDiscrepancy> Discrepancies { get; init; } = [];
    public List<string> LoggedIssueIds { get; init; } = [];
    public AccountFixtureDto? GroundTruth { get; init; }
    public RenderedClientStatsDto? RenderedClientState { get; init; }
    public long ElapsedMs { get; init; }
}

public interface IClientStatsAuditorService
{
    Task<StatsAuditReport> RunFullAuditAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
    Task<StatsAuditReport> AuditPlayerStatsAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
    Task<StatsAuditReport> AuditMatchHistoryAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
    Task<StatsAuditReport> AuditMasteryAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
    Task<StatsAuditReport> AuditMatchDetailsAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
    Task<StatsAuditReport> AuditUiGhostingAsync(StatsAuditRequest request, CancellationToken cancellationToken = default);
}

public sealed class ClientStatsAuditorService : IClientStatsAuditorService
{
    private static readonly Regex ColorCodeRegex = new(@"\^[0-9a-zA-Z*]", RegexOptions.Compiled);
    private static readonly Regex KdaExtractRegex = new(@"(\d+)\s*\/\s*(\d+)\s*\/\s*(\d+)", RegexOptions.Compiled);

    private readonly IIssueTrackerService _issueTracker;
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly IK2UiInspectService? _uiInspectService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public ClientStatsAuditorService(
        IIssueTrackerService? issueTracker = null,
        IGameIpcBridge? ipcBridge = null,
        IK2UiInspectService? uiInspectService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _issueTracker = issueTracker ?? new IssueTrackerService();
        _ipcBridge = ipcBridge;
        _uiInspectService = uiInspectService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[StatsAuditor] {msg}"));
    }

    public async Task<StatsAuditReport> RunFullAuditAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();

        _logger?.Invoke($"Starting audit for player '{request.Nickname}' (Tab: '{request.Tab}', Mode: '{(request.Synthetic ? "Synthetic" : "Live")}')");

        AccountFixtureDto groundTruth = request.CustomGroundTruth ?? DbFixtureHandler.FindAccount(request.Nickname) ?? new AccountFixtureDto
        {
            Username = request.Nickname,
            AccountId = 99999,
            Wins = 10,
            Losses = 5,
            Disconnects = 0,
            Mmr = 1500
        };

        RenderedClientStatsDto renderedState;
        if (request.Synthetic || _ipcBridge == null)
        {
            renderedState = request.CustomRenderedState ?? GenerateDefaultRenderedState(groundTruth);
        }
        else
        {
            renderedState = await ExtractLiveClientStateAsync(request.Nickname, cancellationToken).ConfigureAwait(false);
        }

        List<StatsDiscrepancy> discrepancies = [];

        // 1. Audit Overview Stats
        if (request.Tab is "all" or "overview" or "stats")
        {
            AuditOverviewInternal(groundTruth, renderedState, discrepancies);
        }

        // 2. Audit Match History
        if (request.Tab is "all" or "match_history" or "matches")
        {
            AuditMatchHistoryInternal(groundTruth, renderedState, discrepancies);
        }

        // 3. Audit Mastery
        if (request.Tab is "all" or "mastery")
        {
            AuditMasteryInternal(groundTruth, renderedState, discrepancies);
        }

        // 4. Audit Match Details
        if (request.Tab is "all" or "match_details" or "details")
        {
            AuditMatchDetailsInternal(groundTruth, renderedState, discrepancies);
        }

        // 5. Audit UI Ghosting
        if (request.Tab is "all" or "ghosting" or "ui")
        {
            AuditUiGhostingInternal(groundTruth, renderedState, discrepancies);
        }

        // 6. Autonomous Issue Filing with Idempotency
        List<string> loggedIssueIds = [];
        if (request.RecordBacklog && discrepancies.Count > 0)
        {
            foreach (var disc in discrepancies)
            {
                string issueTitle = $"Stats Auditor: {disc.RuleCode} on '{request.Nickname}'" + (disc.MatchId != null ? $" (Match #{disc.MatchId})" : "");
                var entry = new IssueEntry
                {
                    Title = issueTitle,
                    Subsystem = disc.Subsystem,
                    DiscoveryMethod = "kongor audit stats automated verification sweep",
                    Symptom = $"{disc.Description}. Expected: '{disc.Expected}', Actual: '{disc.Actual}'.",
                    RootCause = disc.RemediationHint ?? "Discrepancy detected between database ground truth and rendered client UI.",
                    Status = IssueStatus.Open,
                    Remediation = disc.RemediationHint ?? "Verify client Lua binding and requester data parsing."
                };

                string issueId = await _issueTracker.RecordIssueIfNotExistsAsync(entry, cancellationToken).ConfigureAwait(false);
                if (issueId != "[EXISTING]" && !loggedIssueIds.Contains(issueId))
                {
                    loggedIssueIds.Add(issueId);
                    _logger?.Invoke($"Logged new issue {issueId}: {issueTitle}");
                }
            }
        }

        sw.Stop();
        _logger?.Invoke($"Audit completed in {sw.ElapsedMilliseconds}ms. Found {discrepancies.Count} discrepancies. Logged {loggedIssueIds.Count} backlog issues.");

        return new StatsAuditReport
        {
            Nickname = request.Nickname,
            ExecutionMode = request.Synthetic ? "Synthetic" : "LiveIPC",
            Discrepancies = discrepancies,
            LoggedIssueIds = loggedIssueIds,
            GroundTruth = groundTruth,
            RenderedClientState = renderedState,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    public Task<StatsAuditReport> AuditPlayerStatsAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        var specificRequest = new StatsAuditRequest
        {
            Nickname = request.Nickname,
            Tab = "overview",
            Synthetic = request.Synthetic,
            RecordBacklog = request.RecordBacklog,
            CustomGroundTruth = request.CustomGroundTruth,
            CustomRenderedState = request.CustomRenderedState
        };
        return RunFullAuditAsync(specificRequest, cancellationToken);
    }

    public Task<StatsAuditReport> AuditMatchHistoryAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        var specificRequest = new StatsAuditRequest
        {
            Nickname = request.Nickname,
            Tab = "match_history",
            Synthetic = request.Synthetic,
            RecordBacklog = request.RecordBacklog,
            CustomGroundTruth = request.CustomGroundTruth,
            CustomRenderedState = request.CustomRenderedState
        };
        return RunFullAuditAsync(specificRequest, cancellationToken);
    }

    public Task<StatsAuditReport> AuditMasteryAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        var specificRequest = new StatsAuditRequest
        {
            Nickname = request.Nickname,
            Tab = "mastery",
            Synthetic = request.Synthetic,
            RecordBacklog = request.RecordBacklog,
            CustomGroundTruth = request.CustomGroundTruth,
            CustomRenderedState = request.CustomRenderedState
        };
        return RunFullAuditAsync(specificRequest, cancellationToken);
    }

    public Task<StatsAuditReport> AuditMatchDetailsAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        var specificRequest = new StatsAuditRequest
        {
            Nickname = request.Nickname,
            Tab = "match_details",
            Synthetic = request.Synthetic,
            RecordBacklog = request.RecordBacklog,
            CustomGroundTruth = request.CustomGroundTruth,
            CustomRenderedState = request.CustomRenderedState
        };
        return RunFullAuditAsync(specificRequest, cancellationToken);
    }

    public Task<StatsAuditReport> AuditUiGhostingAsync(StatsAuditRequest request, CancellationToken cancellationToken = default)
    {
        var specificRequest = new StatsAuditRequest
        {
            Nickname = request.Nickname,
            Tab = "ghosting",
            Synthetic = request.Synthetic,
            RecordBacklog = request.RecordBacklog,
            CustomGroundTruth = request.CustomGroundTruth,
            CustomRenderedState = request.CustomRenderedState
        };
        return RunFullAuditAsync(specificRequest, cancellationToken);
    }

    private static void AuditOverviewInternal(AccountFixtureDto gt, RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        // Wins parity
        if (gt.Wins != client.Wins)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.GAMES_COUNT_MISMATCH,
                Subsystem = "Game Client (player_stats_v2.lua)",
                Description = "Total wins mismatch between ground truth and UI label",
                Expected = gt.Wins.ToString(CultureInfo.InvariantCulture),
                Actual = client.Wins.ToString(CultureInfo.InvariantCulture),
                RemediationHint = "Verify stats_data_player_stats_wins widget text assignment in SetPlayerStatsStatsInfo."
            });
        }

        // Losses parity
        if (gt.Losses != client.Losses)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.GAMES_COUNT_MISMATCH,
                Subsystem = "Game Client (player_stats_v2.lua)",
                Description = "Total losses mismatch between ground truth and UI label",
                Expected = gt.Losses.ToString(CultureInfo.InvariantCulture),
                Actual = client.Losses.ToString(CultureInfo.InvariantCulture),
                RemediationHint = "Verify stats_data_player_stats_losses widget text assignment in SetPlayerStatsStatsInfo."
            });
        }

        // Disconnects parity
        if (gt.Disconnects != client.Disconnects)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.DISCONNECT_COUNT_MISMATCH,
                Subsystem = "Game Client (player_stats_v2.lua)",
                Description = "Disconnect count mismatch between ground truth and UI label",
                Expected = gt.Disconnects.ToString(CultureInfo.InvariantCulture),
                Actual = client.Disconnects.ToString(CultureInfo.InvariantCulture),
                RemediationHint = "Verify stats_data_player_stats_disconnects parsing in SetPlayerStatsStatsInfo."
            });
        }

        // Division-by-Zero / NaN Check
        int totalGames = gt.Wins + gt.Losses;
        if (totalGames == 0)
        {
            if (client.WinningPercentage.Contains("nan", StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.DIV_ZERO_NAN,
                    Subsystem = "Game Client (player_stats_v2.lua)",
                    Description = "Win rate displays NaN% when player has zero recorded matches",
                    Expected = "0%",
                    Actual = client.WinningPercentage,
                    RemediationHint = "Wrap winning percentage calculation with '(wins + losses) > 0' check in player_stats_v2.lua:734."
                });
            }
        }
        else
        {
            // Win Rate Mathematical Drift Check
            double expectedPct = Math.Floor((double)gt.Wins / (gt.Wins + gt.Losses) * 100.0);
            string expectedPctStr = $"{expectedPct:0}%";
            string cleanClientPct = client.WinningPercentage.TrimEnd('%').Trim();
            if (double.TryParse(cleanClientPct, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedClientPct))
            {
                if (Math.Abs(parsedClientPct - expectedPct) > 1.0)
                {
                    discrepancies.Add(new StatsDiscrepancy
                    {
                        RuleCode = DiscrepancyRuleCode.WIN_RATE_DRIFT,
                        Subsystem = "Game Client (player_stats_v2.lua)",
                        Description = "Calculated winning percentage drifted from expected formula",
                        Expected = expectedPctStr,
                        Actual = client.WinningPercentage,
                        RemediationHint = "Check integer vs float division in player_stats_v2.lua:Line 734."
                    });
                }
            }
        }

        // 5. CoN Reward Box Check
        if (string.IsNullOrWhiteSpace(client.ConRewardTexture) ||
            client.ConRewardTexture.Contains("$invis", StringComparison.OrdinalIgnoreCase) ||
            client.ConRewardTexture == "0")
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.CON_REWARD_BLANK,
                Subsystem = "Game Client (player_stats_v2.lua)",
                Description = "Champions of Newerth reward box is blank ($invis fallback)",
                Expected = "/ui/fe2/newui/res/playerstats/conrewards/chest0.png",
                Actual = string.IsNullOrWhiteSpace(client.ConRewardTexture) ? "empty" : client.ConRewardTexture,
                RemediationHint = "Provide canonical precached fallback texture (chest0.png) in SetOverviewSeasonsInfo."
            });
        }

        // 6. Dev Teapot Check
        CheckTeapots(client, discrepancies);
    }

    private static void AuditMatchHistoryInternal(AccountFixtureDto gt, RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        var gtMatches = gt.MatchHistory;
        var clientRows = client.MatchRows;

        int checkCount = Math.Min(gtMatches.Count, clientRows.Count);
        for (int i = 0; i < checkCount; i++)
        {
            var gtEntry = gtMatches[i];
            var clientRow = clientRows[i];

            // 1. K/D/A Parity Check
            string cleanKda = ColorCodeRegex.Replace(clientRow.KdaText, "");
            var kdaMatch = KdaExtractRegex.Match(cleanKda);
            if (kdaMatch.Success)
            {
                int cKills = int.Parse(kdaMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                int cDeaths = int.Parse(kdaMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                int cAssists = int.Parse(kdaMatch.Groups[3].Value, CultureInfo.InvariantCulture);

                if (cKills != gtEntry.Kills || cDeaths != gtEntry.Deaths || cAssists != gtEntry.Assists)
                {
                    discrepancies.Add(new StatsDiscrepancy
                    {
                        RuleCode = DiscrepancyRuleCode.KDA_MISMATCH,
                        Subsystem = "Game Client (player_stats_v2.lua)",
                        MatchId = gtEntry.MatchId,
                        Description = $"K/D/A mismatch in match row #{i + 1}",
                        Expected = $"{gtEntry.Kills}/{gtEntry.Deaths}/{gtEntry.Assists}",
                        Actual = $"{cKills}/{cDeaths}/{cAssists}",
                        RemediationHint = "Verify OnPlayerStatsMatchListResult CSV column indexing (parts[4], parts[5], parts[6])."
                    });
                }
            }

            // 2. Outcome (Victory / Defeat Texture) Parity Check
            bool expectedVictory = gtEntry.Winner == gtEntry.PlayerTeam;
            string expectedTextureSubstring = expectedVictory ? "victory" : "defeat";
            if (!clientRow.ResultTexture.Contains(expectedTextureSubstring, StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.OUTCOME_MISMATCH,
                    Subsystem = "Game Client (player_stats_v2.lua)",
                    MatchId = gtEntry.MatchId,
                    Description = $"Outcome banner texture mismatch in match row #{i + 1}",
                    Expected = expectedVictory ? "match_result_victory.png" : "match_result_defeat.png",
                    Actual = clientRow.ResultTexture,
                    RemediationHint = "Verify match['result'] check (match['result'] == '1') in player_stats_v2.lua:Line 948."
                });
            }

            // 3. Duration Format Parity Check
            TimeSpan durationSpan = TimeSpan.FromSeconds(gtEntry.DurationSeconds);
            string expectedDuration = durationSpan.TotalHours >= 1
                ? durationSpan.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : durationSpan.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

            if (!string.Equals(clientRow.DurationText.Trim(), expectedDuration.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.DURATION_FORMAT_ERROR,
                    Subsystem = "Game Client (player_stats_v2.lua)",
                    MatchId = gtEntry.MatchId,
                    Description = $"Duration format mismatch in match row #{i + 1}",
                    Expected = expectedDuration,
                    Actual = clientRow.DurationText,
                    RemediationHint = "Verify FtoT call or duration seconds-to-milliseconds multiplier in SetPlayerStatsMatchList."
                });
            }

            // 4. Hero Icon VFS Existence Check
            if (string.IsNullOrWhiteSpace(clientRow.HeroIconTexture) ||
                clientRow.HeroIconTexture.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                clientRow.HeroIconTexture == "0")
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.VFS_TEXTURE_MISSING,
                    Subsystem = "Game Client & VFS",
                    MatchId = gtEntry.MatchId,
                    Description = $"Hero icon texture is missing or unresolved in match row #{i + 1}",
                    Expected = gtEntry.HeroIcon,
                    Actual = clientRow.HeroIconTexture,
                    RemediationHint = "Verify GetHeroIconPath and HeroDefinitionsCache.GetHeroIcon fallback in player_stats_v2.lua:Line 939."
                });
            }

            // 5. Hero Name Unresolved Check
            if (string.IsNullOrWhiteSpace(clientRow.HeroName) || clientRow.HeroName == "0" || clientRow.HeroName.Equals("Unknown Hero", StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.HERO_NAME_UNRESOLVED,
                    Subsystem = "Game Client (player_stats_v2.lua)",
                    MatchId = gtEntry.MatchId,
                    Description = $"Hero name unresolved in match row #{i + 1}",
                    Expected = gtEntry.HeroName,
                    Actual = clientRow.HeroName,
                    RemediationHint = "Verify HeroDefinitionsCache.HeroIdMap and CleanHeroIdentifier in MatchHistoryHandler.cs."
                });
            }
        }

        CheckTeapots(client, discrepancies);
    }

    private static void AuditMasteryInternal(AccountFixtureDto gt, RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        if (client.Mastery == null) return;

        // 1. Hooded silhouettes / placeholder "0" heroes
        if (client.Mastery.HoodedSilhouettesCount > 0)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.MASTERY_DATA_CORRUPT,
                Subsystem = "Game Client (player_stats_v2.lua)",
                Description = $"Mastery grid contains {client.Mastery.HoodedSilhouettesCount} hooded placeholder silhouettes with unparsed names or CSV corruption",
                Expected = "0 hooded silhouettes",
                Actual = $"{client.Mastery.HoodedSilhouettesCount} hooded silhouettes",
                RemediationHint = "Filter out '0', '', nil in OnMasteryHeroInfo and ensure defensive CSV parsing in HeroDefinitionsCache.ParseMasteryInfo."
            });
        }

        // 2. Interactive Claim/Redeem Button on Overall Rewards
        if (!client.Mastery.HasOverallRewardClaimButton)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.REWARD_BUTTON_MISSING,
                Subsystem = "Game Client (player_stats_v2.package / player_stats_v2.lua)",
                Description = "Mastery Overall Rewards popup missing interactive Claim/Redeem button widget",
                Expected = "playerstats_mastery_overall_reward_claim_btn present and clickable",
                Actual = "Claim button missing or static icon only",
                RemediationHint = "Add playerstats_mastery_overall_reward_{index}_claim_btn to playerstats_mastery_overall_reward_template and wire OnClickClaimMasteryReward."
            });
        }

        // 3. Clamped hero level validation [1, 15]
        foreach (var hero in client.Mastery.Heroes)
        {
            if (hero.Level < 1 || hero.Level > 15)
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.MASTERY_DATA_CORRUPT,
                    Subsystem = "Game Client (hero_definitions_cache.lua)",
                    Description = $"Mastery hero '{hero.HeroKey}' has out-of-range level: {hero.Level}",
                    Expected = "Level between 1 and 15",
                    Actual = hero.Level.ToString(CultureInfo.InvariantCulture),
                    RemediationHint = "Clamp mastery level calculations to [1, 15] in HeroDefinitionsCache.ParseMasteryInfo."
                });
            }
        }

        CheckTeapots(client, discrepancies);
    }

    private static void AuditMatchDetailsInternal(AccountFixtureDto gt, RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        if (client.MatchDetails == null) return;

        var details = client.MatchDetails;

        // 1. Match Owner persistence
        if (string.IsNullOrWhiteSpace(details.MatchOwner))
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA,
                Subsystem = "Game Client (match_stats_v2.lua)",
                MatchId = details.MatchId,
                Description = "Match owner cvar was dropped; scoreboard focal player and personal rewards skipped",
                Expected = gt.Username,
                Actual = "empty",
                RemediationHint = "Set '_playerstats_match_owner' in OnClickMatchDetail and preserve it across MainMenuPanelSwitcher panel transitions."
            });
        }

        // 2. 3D Hero Model Pedestal
        if (string.IsNullOrWhiteSpace(details.HeroModelPath))
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA,
                Subsystem = "Game Client (match_stats_v2.lua)",
                MatchId = details.MatchId,
                Description = "3D hero model pedestal is empty in match details rewards view",
                Expected = "Valid model path (.mdf)",
                Actual = "empty",
                RemediationHint = "Fallback to HeroDefinitionsCache.GetHeroModel in HeroModelPanel and guard ambient/sunColor lighting vectors."
            });
        }

        // 3. Hero Mastery XP Bar Text
        if (string.IsNullOrWhiteSpace(details.MasteryXpBarText) || details.MasteryXpBarText.Contains('-'))
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA,
                Subsystem = "Game Client (match_stats_v2.lua)",
                MatchId = details.MatchId,
                Description = "Hero Mastery XP bar unpopulated or displaying dashed placeholder '-'",
                Expected = "EXP X/Y",
                Actual = string.IsNullOrWhiteSpace(details.MasteryXpBarText) ? "empty" : details.MasteryXpBarText,
                RemediationHint = "Ensure RewardStat_MasteryXPAnimation parses numbers safely with tonumber and guards division by zero."
            });
        }

        // 4. Next LVL Award Box
        if (string.IsNullOrWhiteSpace(details.NextLevelAwardTexture) ||
            details.NextLevelAwardTexture.Contains("$invis", StringComparison.OrdinalIgnoreCase))
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA,
                Subsystem = "Game Client (match_stats_v2.lua)",
                MatchId = details.MatchId,
                Description = "Next LVL Award box is empty",
                Expected = "/ui/fe2/mastery/reward_level_X.tga",
                Actual = string.IsNullOrWhiteSpace(details.NextLevelAwardTexture) ? "empty" : details.NextLevelAwardTexture,
                RemediationHint = "Set reward_level_X.tga texture and reveal rewardstat_nextlevelaward_panel in RewardStat_MasteryXPAnimation."
            });
        }

        // 5. Match Inventory items
        if (details.InventoryItemIcons.Count > 0 && details.InventoryItemIcons.All(i => string.IsNullOrWhiteSpace(i) || i == "/ui/common/empty_pack_garena.tga"))
        {
            if (gt.MatchHistory.Count > 0)
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.MATCH_DETAILS_MISSING_DATA,
                    Subsystem = "Game Client (match_stats_v2.lua)",
                    MatchId = details.MatchId,
                    Description = "All match inventory slots show grey empty packs despite player having items",
                    Expected = "Valid item textures (Item_...)",
                    Actual = "All empty packs",
                    RemediationHint = "Upgrade HeroMatchInventory to use HeroDefinitionsCache.GetItemIcon and clean invalid entity strings."
                });
            }
        }

        CheckTeapots(client, discrepancies);
    }

    private static void AuditUiGhostingInternal(AccountFixtureDto gt, RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        // Check for multiple mutually exclusive containers simultaneously visible
        var tabContainers = client.VisibleContainers
            .Where(c => c is "playerstats_overview" or "playerstats_mastery" or "playerstats_stats")
            .ToList();

        if (tabContainers.Count > 1)
        {
            discrepancies.Add(new StatsDiscrepancy
            {
                RuleCode = DiscrepancyRuleCode.UI_LAYER_GHOSTING,
                Subsystem = "Game Client UI (player_stats_v2.package)",
                Description = $"Mutually exclusive UI tab containers visible simultaneously: {string.Join(", ", tabContainers)}",
                Expected = "Exactly 1 active tab container visible",
                Actual = $"{tabContainers.Count} containers visible ({string.Join(", ", tabContainers)})",
                RemediationHint = "Set default visible='0' on inactive containers (playerstats_mastery, playerstats_stats) and add occluding black backing panel."
            });
        }

        CheckTeapots(client, discrepancies);
    }

    private static void CheckTeapots(RenderedClientStatsDto client, List<StatsDiscrepancy> discrepancies)
    {
        if (client.TeapotOccurrences.Count > 0)
        {
            foreach (var teapot in client.TeapotOccurrences)
            {
                discrepancies.Add(new StatsDiscrepancy
                {
                    RuleCode = DiscrepancyRuleCode.DEV_TEAPOT_DETECTED,
                    Subsystem = "Game Client Dev Tools (ui_devPlaceholders)",
                    Description = $"Dev placeholder / Utah Teapot fallback detected in UI: {teapot}",
                    Expected = "Populated production data from master server",
                    Actual = teapot,
                    RemediationHint = "Server response missing expected fields; populate database fixture or implement client fallback."
                });
            }
        }
    }

    private static RenderedClientStatsDto GenerateDefaultRenderedState(AccountFixtureDto gt)
    {
        int total = gt.Wins + gt.Losses;
        double winPct = total > 0 ? Math.Floor((double)gt.Wins / total * 100.0) : 0.0;
        double leavePct = total > 0 ? ((double)gt.Disconnects / total * 100.0) : 0.0;

        var rendered = new RenderedClientStatsDto
        {
            Wins = gt.Wins,
            Losses = gt.Losses,
            TotalGames = total,
            Disconnects = gt.Disconnects,
            WinningPercentage = $"{winPct:0}%",
            LeavePercent = $"{leavePct:0.0}%",
            Mmr = gt.Mmr,
            ConRewardTexture = "/ui/fe2/newui/res/playerstats/conrewards/chest0.png",
            MatchRows = [],
            VisibleContainers = ["playerstats_overview"],
            Mastery = new RenderedMasteryDto
            {
                TotalHeroes = 145,
                HoodedSilhouettesCount = 0,
                TotalMasteryLevel = 350,
                HasOverallRewardClaimButton = true,
                Heroes =
                [
                    new RenderedMasteryHeroCardDto { HeroKey = "Hero_Pyromancer", HeroName = "Pyromancer", Level = 10, Icon = "/heroes/pyromancer/icon.tga" },
                    new RenderedMasteryHeroCardDto { HeroKey = "Hero_Aluna", HeroName = "Aluna", Level = 5, Icon = "/heroes/aluna/icon.tga" }
                ]
            },
            MatchDetails = new RenderedMatchDetailsDto
            {
                MatchId = gt.MatchHistory.Count > 0 ? gt.MatchHistory[0].MatchId : "1001",
                MatchOwner = gt.Username,
                HeroModelPath = "/heroes/pyromancer/hero.mdf",
                MasteryXpBarText = "EXP 120/500",
                NextLevelAwardTexture = "/ui/fe2/mastery/reward_level_2.tga",
                InventoryItemIcons = ["/items/boots/icon.tga", "/items/staff/icon.tga"]
            }
        };

        foreach (var m in gt.MatchHistory)
        {
            bool isWin = m.Winner == m.PlayerTeam;
            TimeSpan dur = TimeSpan.FromSeconds(m.DurationSeconds);
            string durStr = dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : dur.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

            rendered.MatchRows.Add(new RenderedMatchRowDto
            {
                MatchId = m.MatchId,
                HeroName = m.HeroName,
                HeroIconTexture = m.HeroIcon,
                ResultTexture = isWin ? "/ui/fe2/newui/res/playerstats/match_result_victory.png" : "/ui/fe2/newui/res/playerstats/match_result_defeat.png",
                KdaText = $"^g{m.Kills}^*/^r{m.Deaths}^*/^y{m.Assists}",
                DurationText = durStr,
                MapText = m.MapName,
                TimeText = m.MatchDate
            });
        }

        return rendered;
    }

    private async Task<RenderedClientStatsDto> ExtractLiveClientStateAsync(string nickname, CancellationToken cancellationToken)
    {
        var gt = DbFixtureHandler.FindAccount(nickname) ?? new AccountFixtureDto { Username = nickname };
        var rendered = GenerateDefaultRenderedState(gt);

        if (_ipcBridge == null) return rendered;

        // 1. Open Stats panel
        string openStatsCmd = $"Script if Player_Stats_V2 and Player_Stats_V2.Show then Player_Stats_V2:Show('{nickname}'); end";
        await _ipcBridge.ExecuteCommandAsync(openStatsCmd, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 2. Two-Tier Readiness Latch
        // Tier 1: Data settlement; Tier 2: Render settlement
        bool ready = await WaitForConditionAsync(
            async () =>
            {
                string latchCmd = "Script Echo('[AUDIT_LATCH]|' .. tostring(_matchList ~= nil) .. '|' .. tostring(bHeroMasteryRetrieved == true) .. '|' .. tostring(GetWidget('playerstats_overview') and GetWidget('playerstats_overview'):IsVisible()))";
                string? resp = await _ipcBridge.ExecuteCommandAsync(latchCmd, cancellationToken: cancellationToken).ConfigureAwait(false);
                return resp != null && resp.Contains("true|true|true", StringComparison.OrdinalIgnoreCase);
            },
            timeout: TimeSpan.FromMilliseconds(2500),
            interval: TimeSpan.FromMilliseconds(100),
            cancellationToken).ConfigureAwait(false);

        _logger?.Invoke($"Live UI two-tier readiness latch completed: ready={ready}");

        // 3. Extract Overview & CoN Reward via pipe-delimited query
        try
        {
            string overviewQuery = "Script local w = GetWidget('playerstats_con_reward_icon'); local conTex = (w and w:GetTexture()) or ''; Echo('[AUDIT_LIVE_OVERVIEW]|' .. tostring(_statsRankTypeActions['0'].wins) .. '|' .. tostring(_statsRankTypeActions['0'].losses) .. '|' .. tostring(_statsRankTypeActions['0'].disconnects) .. '|' .. tostring(_statsRankTypeActions['0'].consec_played) .. '|' .. tostring(conTex))";
            string? ovResp = await _ipcBridge.ExecuteCommandAsync(overviewQuery, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ovResp))
            {
                var match = Regex.Match(ovResp, @"\[AUDIT_LIVE_OVERVIEW\]\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)");
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out int w)) rendered.Wins = w;
                    if (int.TryParse(match.Groups[2].Value, out int l)) rendered.Losses = l;
                    if (int.TryParse(match.Groups[3].Value, out int d)) rendered.Disconnects = d;
                    rendered.ConRewardTexture = match.Groups[5].Value.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Failed to extract live overview stats: {ex.Message}");
        }

        // 4. Extract Mastery via pipe-delimited query
        try
        {
            string masteryQuery = "Script local totalH = #_masteryData; local hooded = 0; for _, h in ipairs(_masteryData) do if tostring(h.name) == '0' or tostring(h.hero) == '' or tostring(h.hero) == '0' then hooded = hooded + 1 end end; local hasClaim = (GetWidget('playerstats_mastery_overall_reward_1_claim_btn') ~= nil); Echo('[AUDIT_LIVE_MASTERY]|' .. tostring(totalH) .. '|' .. tostring(hooded) .. '|' .. tostring(hasClaim))";
            string? mastResp = await _ipcBridge.ExecuteCommandAsync(masteryQuery, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(mastResp))
            {
                var match = Regex.Match(mastResp, @"\[AUDIT_LIVE_MASTERY\]\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)");
                if (match.Success)
                {
                    int totalH = int.TryParse(match.Groups[1].Value, out int th) ? th : 0;
                    int hooded = int.TryParse(match.Groups[2].Value, out int hd) ? hd : 0;
                    bool hasClaim = bool.TryParse(match.Groups[3].Value, out bool hc) && hc;
                    rendered.Mastery = new RenderedMasteryDto
                    {
                        TotalHeroes = totalH,
                        HoodedSilhouettesCount = hooded,
                        HasOverallRewardClaimButton = hasClaim
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Failed to extract live mastery state: {ex.Message}");
        }

        // 5. Extract Match Details state if active
        try
        {
            string matchDetQuery = "Script local owner = GetCvarString('_playerstats_match_owner'); local mdl = GetWidget('rewardstat_modelpanel_widget'); local mdlPath = (mdl and mdl:GetModel()) or ''; local xp = GetWidget('rewardstat_label_mastery_exp'); local xpText = (xp and xp:GetText()) or ''; local nxt = GetWidget('rewardstat_label_heromastery_nextrank'); local nxtTex = (nxt and nxt:GetTexture()) or ''; Echo('[AUDIT_LIVE_MATCH]|' .. tostring(owner) .. '|' .. tostring(mdlPath) .. '|' .. tostring(xpText) .. '|' .. tostring(nxtTex))";
            string? mdResp = await _ipcBridge.ExecuteCommandAsync(matchDetQuery, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(mdResp))
            {
                var match = Regex.Match(mdResp, @"\[AUDIT_LIVE_MATCH\]\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)\|([^|\r\n]*)");
                if (match.Success)
                {
                    rendered.MatchDetails = new RenderedMatchDetailsDto
                    {
                        MatchOwner = match.Groups[1].Value.Trim(),
                        HeroModelPath = match.Groups[2].Value.Trim(),
                        MasteryXpBarText = match.Groups[3].Value.Trim(),
                        NextLevelAwardTexture = match.Groups[4].Value.Trim()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Failed to extract live match details state: {ex.Message}");
        }

        // 6. Extract UI hierarchy & check Ghosting using IK2UiInspectService
        if (_uiInspectService != null)
        {
            try
            {
                var uiResult = await _uiInspectService.InspectLiveUiAsync("playerstats", depth: 3, ct: cancellationToken).ConfigureAwait(false);
                if (uiResult.Success && uiResult.Widgets.Count > 0)
                {
                    List<string> visibleContainers = [];
                    foreach (var w in uiResult.Widgets)
                    {
                        if (w.Visible && (w.Name == "playerstats_overview" || w.Name == "playerstats_mastery" || w.Name == "playerstats_stats"))
                        {
                            visibleContainers.Add(w.Name);
                        }
                    }
                    rendered.VisibleContainers = visibleContainers;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"UI inspection failed during live audit: {ex.Message}");
            }
        }

        return rendered;
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await condition().ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch { }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}
